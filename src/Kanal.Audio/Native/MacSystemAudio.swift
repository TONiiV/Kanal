import AudioToolbox
import CoreAudio
import CoreMedia
import Foundation
import ScreenCaptureKit

public typealias FrameCallback = @convention(c) (
    UnsafePointer<Int16>?, Int32, Int32, UnsafeMutableRawPointer?
) -> Void
public typealias ErrorCallback = @convention(c) (
    UnsafePointer<CChar>?, UnsafeMutableRawPointer?
) -> Void
public typealias StopCallback = @convention(c) (UnsafeMutableRawPointer?) -> Void

private enum NativeBackend: Int32 {
    case coreAudioProcessTap = 2
    case screenCaptureKit = 3
}

private struct SendablePointer: @unchecked Sendable {
    let value: UnsafeMutableRawPointer?
}

private protocol SystemAudioSession: AnyObject, Sendable {
    func start() throws
    func stop(completion: @escaping @Sendable () -> Void)
}

private final class CaptureHandle: @unchecked Sendable {
    private let errorCallback: ErrorCallback
    private let context: UnsafeMutableRawPointer?
    private let queue = DispatchQueue(label: "app.kanal.system-audio.lifecycle")
    private var session: SystemAudioSession?
    private var stopped = false

    init(
        backend: NativeBackend,
        outputDeviceUID: String?,
        frame: @escaping FrameCallback,
        error: @escaping ErrorCallback,
        context: UnsafeMutableRawPointer?
    ) {
        self.errorCallback = error
        self.context = context

        switch backend {
        case .coreAudioProcessTap:
            if #available(macOS 14.2, *) {
                session = CoreAudioTapSession(
                    outputDeviceUID: outputDeviceUID,
                    frame: frame,
                    context: context)
            } else {
                session = UnsupportedSession("Core Audio process taps require macOS 14.2 or later")
            }
        case .screenCaptureKit:
            session = ScreenCaptureKitSession(frame: frame, error: error, context: context)
        }
    }

    func start() {
        queue.async { [weak self] in
            guard let self, !self.stopped, let session = self.session else { return }
            do {
                try session.start()
            } catch {
                self.report(error)
                session.stop {}
            }
        }
    }

    func stop(completion: @escaping @Sendable () -> Void) {
        queue.async {
            guard !self.stopped else {
                completion()
                return
            }
            self.stopped = true
            let active = self.session
            self.session = nil
            active?.stop(completion: completion) ?? completion()
        }
    }

    private func report(_ value: Error) {
        String(describing: value).withCString { errorCallback($0, context) }
    }
}

@_cdecl("kanal_system_audio_start")
public func kanalSystemAudioStart(
    _ backend: Int32,
    _ outputDeviceUID: UnsafePointer<CChar>?,
    _ frame: @escaping FrameCallback,
    _ error: @escaping ErrorCallback,
    _ context: UnsafeMutableRawPointer?
) -> UnsafeMutableRawPointer? {
    guard let backend = NativeBackend(rawValue: backend) else {
        "unsupported macOS audio backend".withCString { error($0, context) }
        return nil
    }

    let uid = outputDeviceUID.map { String(cString: $0) }
    let handle = CaptureHandle(
        backend: backend,
        outputDeviceUID: uid,
        frame: frame,
        error: error,
        context: context)
    let opaque = Unmanaged.passRetained(handle).toOpaque()
    handle.start()
    return opaque
}

@_cdecl("kanal_system_audio_stop_with_completion")
public func kanalSystemAudioStopWithCompletion(
    _ opaque: UnsafeMutableRawPointer?,
    _ completion: @escaping StopCallback,
    _ context: UnsafeMutableRawPointer?
) {
    guard let opaque else {
        completion(context)
        return
    }
    let handle = Unmanaged<CaptureHandle>.fromOpaque(opaque).takeRetainedValue()
    let sendableContext = SendablePointer(value: context)
    handle.stop { completion(sendableContext.value) }
}

@available(macOS 14.2, *)
private final class CoreAudioTapSession: @unchecked Sendable, SystemAudioSession {
    private let outputDeviceUID: String?
    private let frame: FrameCallback
    private let context: UnsafeMutableRawPointer?
    private let callbackQueue = DispatchQueue(label: "app.kanal.system-audio.tap", qos: .userInitiated)
    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggregateDeviceID = AudioObjectID(kAudioObjectUnknown)
    private var ioProcID: AudioDeviceIOProcID?
    private var format = AudioStreamBasicDescription()

    init(
        outputDeviceUID: String?,
        frame: @escaping FrameCallback,
        context: UnsafeMutableRawPointer?
    ) {
        self.outputDeviceUID = outputDeviceUID
        self.frame = frame
        self.context = context
    }

    func start() throws {
        let uid = try outputDeviceUID ?? Self.defaultOutputDeviceUID()
        let description = CATapDescription(
            excludingProcesses: [],
            deviceUID: uid,
            stream: 0)
        description.name = "Kanal computer audio"
        description.uuid = UUID()
        description.isPrivate = true
        description.muteBehavior = .unmuted

        try checked(
            AudioHardwareCreateProcessTap(description, &tapID),
            "AudioHardwareCreateProcessTap")
        do {
            try readTapFormat()
            try createAggregateDevice(outputUID: uid, tapUUID: description.uuid.uuidString)
            try createAndStartIOProc()
        } catch {
            stop {}
            throw error
        }
    }

    func stop(completion: @escaping @Sendable () -> Void) {
        if aggregateDeviceID != kAudioObjectUnknown {
            _ = AudioDeviceStop(aggregateDeviceID, ioProcID)
            if let ioProcID {
                _ = AudioDeviceDestroyIOProcID(aggregateDeviceID, ioProcID)
                self.ioProcID = nil
            }
            _ = AudioHardwareDestroyAggregateDevice(aggregateDeviceID)
            aggregateDeviceID = kAudioObjectUnknown
        }
        if tapID != kAudioObjectUnknown {
            _ = AudioHardwareDestroyProcessTap(tapID)
            tapID = kAudioObjectUnknown
        }
        completion()
    }

    private func readTapFormat() throws {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioTapPropertyFormat,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        try checked(
            AudioObjectGetPropertyData(tapID, &address, 0, nil, &size, &format),
            "read process-tap format")
    }

    private func createAggregateDevice(outputUID: String, tapUUID: String) throws {
        let aggregateUID = "app.kanal.tap.\(UUID().uuidString)"
        let description: [String: Any] = [
            kAudioAggregateDeviceNameKey: "Kanal computer audio",
            kAudioAggregateDeviceUIDKey: aggregateUID,
            kAudioAggregateDeviceMainSubDeviceKey: outputUID,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [
                [kAudioSubDeviceUIDKey: outputUID]
            ],
            kAudioAggregateDeviceTapListKey: [
                [
                    kAudioSubTapUIDKey: tapUUID,
                    kAudioSubTapDriftCompensationKey: true
                ]
            ]
        ]
        try checked(
            AudioHardwareCreateAggregateDevice(description as CFDictionary, &aggregateDeviceID),
            "AudioHardwareCreateAggregateDevice")
    }

    private func createAndStartIOProc() throws {
        let callback: AudioDeviceIOBlock = { [weak self] _, input, _, _, _ in
            self?.emit(input)
        }
        try checked(
            AudioDeviceCreateIOProcIDWithBlock(&ioProcID, aggregateDeviceID, callbackQueue, callback),
            "AudioDeviceCreateIOProcIDWithBlock")
        try checked(AudioDeviceStart(aggregateDeviceID, ioProcID), "AudioDeviceStart")
    }

    private func emit(_ list: UnsafePointer<AudioBufferList>) {
        emitMonoPcm16(
            list: list,
            format: format,
            frame: frame,
            context: context)
    }

    private static func defaultOutputDeviceUID() throws -> String {
        var deviceID = AudioObjectID(kAudioObjectUnknown)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDefaultOutputDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        try checked(
            AudioObjectGetPropertyData(
                AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &deviceID),
            "read default output device")

        var uidAddress = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDeviceUID,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var uid: Unmanaged<CFString>?
        size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        try checked(
            AudioObjectGetPropertyData(deviceID, &uidAddress, 0, nil, &size, &uid),
            "read output device UID")
        guard let uid else {
            throw NativeAudioError.message("default output device has no persistent UID")
        }
        return uid.takeUnretainedValue() as String
    }
}

@available(macOS 13.0, *)
private final class ScreenCaptureKitSession: NSObject, @unchecked Sendable, SystemAudioSession, SCStreamOutput, SCStreamDelegate {
    private let frame: FrameCallback
    private let errorCallback: ErrorCallback
    private let context: UnsafeMutableRawPointer?
    private let callbackQueue = DispatchQueue(label: "app.kanal.system-audio.sck", qos: .userInitiated)
    private let stateLock = NSLock()
    private var stream: SCStream?
    private var stopped = false
    private var format = AudioStreamBasicDescription()

    init(
        frame: @escaping FrameCallback,
        error: @escaping ErrorCallback,
        context: UnsafeMutableRawPointer?
    ) {
        self.frame = frame
        self.errorCallback = error
        self.context = context
    }

    func start() throws {
        SCShareableContent.getExcludingDesktopWindows(false, onScreenWindowsOnly: false) { [weak self] content, error in
            guard let self else { return }
            guard !self.isStopped else { return }
            if let error {
                self.report(error)
                return
            }
            guard let display = content?.displays.first else {
                self.report(NativeAudioError.message(
                    "ScreenCaptureKit found no display to anchor audio capture"))
                return
            }

            let filter = SCContentFilter(display: display, excludingWindows: [])
            let configuration = SCStreamConfiguration()
            configuration.width = 2
            configuration.height = 2
            configuration.showsCursor = false
            configuration.queueDepth = 3
            configuration.capturesAudio = true
            configuration.excludesCurrentProcessAudio = true
            configuration.sampleRate = 48_000
            configuration.channelCount = 1

            let candidate = SCStream(filter: filter, configuration: configuration, delegate: self)
            do {
                try candidate.addStreamOutput(self, type: .audio, sampleHandlerQueue: self.callbackQueue)
            } catch {
                self.report(error)
                return
            }

            self.stateLock.lock()
            guard !self.stopped else {
                self.stateLock.unlock()
                return
            }
            self.stream = candidate
            candidate.startCapture { [weak self] error in
                guard let self, !self.isStopped, let error else { return }
                self.report(error)
            }
            self.stateLock.unlock()
        }
    }

    func stop(completion: @escaping @Sendable () -> Void) {
        stateLock.lock()
        stopped = true
        let running = stream
        stream = nil
        stateLock.unlock()

        guard let running else {
            completion()
            return
        }
        running.stopCapture { _ in completion() }
    }

    func stream(
        _ stream: SCStream,
        didOutputSampleBuffer sampleBuffer: CMSampleBuffer,
        of outputType: SCStreamOutputType
    ) {
        guard !isStopped,
              outputType == .audio,
              CMSampleBufferDataIsReady(sampleBuffer),
              let description = CMSampleBufferGetFormatDescription(sampleBuffer),
              let basic = CMAudioFormatDescriptionGetStreamBasicDescription(description)
        else { return }

        format = basic.pointee
        var retainedBlock: CMBlockBuffer?
        var storage = AudioBufferList(
            mNumberBuffers: 1,
            mBuffers: AudioBuffer(mNumberChannels: 0, mDataByteSize: 0, mData: nil))
        let status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer,
            bufferListSizeNeededOut: nil,
            bufferListOut: &storage,
            bufferListSize: MemoryLayout<AudioBufferList>.size,
            blockBufferAllocator: nil,
            blockBufferMemoryAllocator: nil,
            flags: 0,
            blockBufferOut: &retainedBlock)
        guard status == noErr else {
            report(NativeAudioError.status("read ScreenCaptureKit audio buffer", status))
            return
        }
        withUnsafePointer(to: &storage) {
            emitMonoPcm16(list: $0, format: format, frame: frame, context: context)
        }
        _ = retainedBlock
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        if !isStopped {
            report(error)
        }
    }

    private func report(_ value: Error) {
        String(describing: value).withCString { errorCallback($0, context) }
    }

    private var isStopped: Bool {
        stateLock.lock()
        defer { stateLock.unlock() }
        return stopped
    }

}

private func emitMonoPcm16(
    list: UnsafePointer<AudioBufferList>,
    format: AudioStreamBasicDescription,
    frame: FrameCallback,
    context: UnsafeMutableRawPointer?
) {
    let buffers = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: list))
    guard !buffers.isEmpty else { return }
    let bytesPerSample = Int(format.mBitsPerChannel / 8)
    guard bytesPerSample == 2 || bytesPerSample == 4 else { return }

    var frameCount = Int.max
    for buffer in buffers where buffer.mNumberChannels > 0 {
        frameCount = min(
            frameCount,
            Int(buffer.mDataByteSize) / (bytesPerSample * Int(buffer.mNumberChannels)))
    }
    guard frameCount > 0, frameCount < Int.max else { return }

    let isFloat = (format.mFormatFlags & kAudioFormatFlagIsFloat) != 0
    var mono = [Int16](repeating: 0, count: frameCount)
    for index in 0..<frameCount {
        var sum = 0.0
        var channelCount = 0
        for buffer in buffers {
            guard let data = buffer.mData else { continue }
            let channels = Int(buffer.mNumberChannels)
            for channel in 0..<channels {
                let offset = (index * channels) + channel
                if isFloat && bytesPerSample == 4 {
                    sum += Double(data.assumingMemoryBound(to: Float.self)[offset])
                } else if bytesPerSample == 2 {
                    sum += Double(data.assumingMemoryBound(to: Int16.self)[offset]) / 32_768.0
                }
                channelCount += 1
            }
        }
        let average = channelCount == 0 ? 0 : sum / Double(channelCount)
        let scaled = Int((average * 32_767.0).rounded())
        mono[index] = Int16(clamping: scaled)
    }

    mono.withUnsafeBufferPointer {
        frame($0.baseAddress, Int32($0.count * MemoryLayout<Int16>.size), Int32(format.mSampleRate), context)
    }
}

private enum NativeAudioError: Error, CustomStringConvertible {
    case message(String)
    case status(String, OSStatus)

    var description: String {
        switch self {
        case .message(let message):
            return message
        case .status(let operation, let status):
            return "\(operation) failed (OSStatus \(status))"
        }
    }
}

private final class UnsupportedSession: @unchecked Sendable, SystemAudioSession {
    private let message: String

    init(_ message: String) {
        self.message = message
    }

    func start() throws {
        throw NativeAudioError.message(message)
    }

    func stop(completion: @escaping @Sendable () -> Void) {
        completion()
    }
}

private func checked(_ status: OSStatus, _ operation: String) throws {
    guard status == noErr else { throw NativeAudioError.status(operation, status) }
}
