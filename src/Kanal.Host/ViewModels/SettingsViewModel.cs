using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanal.Host.Services;

namespace Kanal.Host.ViewModels;

public partial class ApiKeyItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _key = "";

    [ObservableProperty]
    private bool _isActive;
}

/// <summary>
/// Manages the stored Gladia API keys. Multiple keys, one active; the env var
/// GLADIA_API_KEY stays as the fallback when no stored key exists.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    public SettingsViewModel()
    {
        var settings = SettingsStore.Load();
        foreach (var entry in settings.ApiKeys.Where(k => k.Provider == "gladia"))
        {
            Keys.Add(new ApiKeyItemViewModel
            {
                Name = entry.Name,
                Key = entry.Key,
                IsActive = entry.Name == settings.ActiveGladiaKeyName,
            });
        }

        if (Keys.Count > 0 && !Keys.Any(k => k.IsActive))
            Keys[0].IsActive = true;

        EnvFallback = SettingsStore.ReadEnvAllScopes(SettingsStore.GladiaEnvVar) is not null
            ? $"Fallback: {SettingsStore.GladiaEnvVar} env var is set."
            : $"Fallback: {SettingsStore.GladiaEnvVar} env var is not set.";
    }

    public ObservableCollection<ApiKeyItemViewModel> Keys { get; } = new();

    public string EnvFallback { get; }

    [ObservableProperty]
    private string _newName = "";

    [ObservableProperty]
    private string _newKey = "";

    [RelayCommand]
    private void Add()
    {
        if (string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(NewKey))
            return;

        var item = new ApiKeyItemViewModel
        {
            Name = NewName.Trim(),
            Key = NewKey.Trim(),
            IsActive = Keys.Count == 0,
        };
        Keys.Add(item);
        NewName = "";
        NewKey = "";
    }

    [RelayCommand]
    private void Remove(ApiKeyItemViewModel item)
    {
        var wasActive = item.IsActive;
        Keys.Remove(item);
        if (wasActive && Keys.Count > 0)
            Keys[0].IsActive = true;
    }

    [RelayCommand]
    private void SetActive(ApiKeyItemViewModel item)
    {
        foreach (var key in Keys)
            key.IsActive = ReferenceEquals(key, item);
    }

    public void Save()
    {
        var settings = SettingsStore.Load();
        settings.ApiKeys.RemoveAll(k => k.Provider == "gladia");
        settings.ApiKeys.AddRange(Keys
            .Where(k => !string.IsNullOrWhiteSpace(k.Name) && !string.IsNullOrWhiteSpace(k.Key))
            .Select(k => new ApiKeyEntry(k.Name.Trim(), "gladia", k.Key.Trim())));
        settings.ActiveGladiaKeyName = Keys.FirstOrDefault(k => k.IsActive)?.Name.Trim();
        SettingsStore.Save(settings);
    }
}
