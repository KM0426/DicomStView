using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using FellowOakDicom;
using Microsoft.Win32;

namespace DicomStView
{

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly List<StudyDicomGroup> _studyGroups = [];

    public MainWindow()
    {
        InitializeComponent();
    }

    private void SelectFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "DICOMファイルを検索するフォルダを選択してください"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SelectedFolderTextBlock.Text = $"選択フォルダ: {dialog.FolderName}";

        var studyFiles = Directory
            .EnumerateFiles(dialog.FolderName, "*", SearchOption.TopDirectoryOnly)
            .Select(TryReadStudyFile)
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();
        
        //debug用
        // foreach (var item in studyFiles)
        // {
        //     // Dateset 確認
        //     var dicomFile = DicomFile.Open(item.FilePath, FileReadOption.ReadLargeOnDemand);
        //     var dataset = dicomFile.Dataset;
        // }

        _studyGroups.Clear();
        _studyGroups.AddRange(
            studyFiles
                .GroupBy(x => x.StudyInstanceUid)
                .Select(g => new StudyDicomGroup(
                    g.Key,
                    g.Select(x => x.Modality)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x)
                        .ToList(),
                    g.Select(x => x.FilePath).ToList()))
                .OrderBy(x => x.StudyInstanceUid));

        DicomFilesListBox.ItemsSource = _studyGroups;

        if (_studyGroups.Count == 0)
        {
            MessageBox.Show("DICOMファイルは見つかりませんでした。", "検索結果", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var fileCount = _studyGroups.Sum(x => x.FilePaths.Count);
        MessageBox.Show($"{_studyGroups.Count} Study / {fileCount} DICOMファイルを取得しました。", "検索結果", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void DicomFilesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DicomFilesListBox.SelectedItem is not StudyDicomGroup selectedStudy)
        {
            return;
        }

        var viewer = new StudyViewerWindow(selectedStudy.StudyInstanceUid, selectedStudy.FilePaths.ToList())
        {
            Owner = this
        };

        _ = viewer.ShowDialog();
    }

    private static StudyFile? TryReadStudyFile(string filePath)
    {
        try
        {
            var dicomFile = DicomFile.Open(filePath, FileReadOption.ReadLargeOnDemand);
            var dataset = dicomFile.Dataset;

            if (!dataset.TryGetSingleValue(DicomTag.StudyInstanceUID, out string studyInstanceUid) || string.IsNullOrWhiteSpace(studyInstanceUid))
            {
                return null;
            }

            var modality = dataset.TryGetSingleValue(DicomTag.Modality, out string value) && !string.IsNullOrWhiteSpace(value)
                ? value.ToUpperInvariant()
                : "UNKNOWN";

            return new StudyFile(filePath, studyInstanceUid, modality);
        }
        catch
        {
            return null;
        }
    }

    private sealed record StudyFile(string FilePath, string StudyInstanceUid, string Modality);

    private sealed class StudyDicomGroup
    {
        public StudyDicomGroup(string studyInstanceUid, IReadOnlyList<string> modalities, IReadOnlyList<string> filePaths)
        {
            StudyInstanceUid = studyInstanceUid;
            Modalities = modalities;
            FilePaths = filePaths;
        }

        public string StudyInstanceUid { get; }

        public IReadOnlyList<string> Modalities { get; }

        public IReadOnlyList<string> FilePaths { get; }

        public bool HasRtStruct => Modalities.Contains("RTSTRUCT", StringComparer.OrdinalIgnoreCase);

        public string ModalitiesDisplay => string.Join(Environment.NewLine, Modalities);
    }
}
}