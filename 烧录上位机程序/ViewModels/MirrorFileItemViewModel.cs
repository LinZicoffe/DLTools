using CommunityToolkit.Mvvm.ComponentModel;
using 烧录上位机程序.Models.Downloads;

namespace 烧录上位机程序.ViewModels;

/// <summary>清单文件列表中的一行（可勾选）。</summary>
public partial class MirrorFileItemViewModel : ObservableObject
{
    public MirrorFileItemViewModel(MirrorFileEntry entry) => Entry = entry;

    public MirrorFileEntry Entry { get; }

    [ObservableProperty] private bool _isSelected;

    public string Name => Entry.Name;
    public string Sha256 => Entry.Sha256;
    public int NodeCount => Entry.Urls.Count;
    public string SizeText => DownloadTaskViewModel.FormatBytes(Entry.Size);
}
