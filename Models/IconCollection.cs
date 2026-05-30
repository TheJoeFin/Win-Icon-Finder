using CommunityToolkit.Mvvm.ComponentModel;

namespace WinIconFinder.Models;

public partial class IconCollection : ObservableObject
{
    public IconCollection(string name, bool isDefault, int iconCount)
    {
        Name = name;
        IsDefault = isDefault;
        IconCount = iconCount;
    }

    public string Name { get; }

    public bool IsDefault { get; }

    public string CountLabel => IconCount == 1 ? "1 icon" : $"{IconCount:N0} icons";

    [ObservableProperty]
    public partial int IconCount { get; set; }

    partial void OnIconCountChanged(int value) => OnPropertyChanged(nameof(CountLabel));
}
