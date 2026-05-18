using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;

namespace DicomStView;

public partial class StudyViewerWindow : Window
{
    private readonly List<SeriesGroup> _seriesGroups = [];
    private readonly List<RtStructDisplayItem> _rtStructItems = [];
    private readonly Dictionary<string, RtStructDisplayItem> _rtStructItemsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ImageSlice> _imageSlices = [];
    private readonly Dictionary<string, List<ContourPolyline>> _contoursBySopInstanceUid = new(StringComparer.Ordinal);
    private readonly List<ContourPolyline> _floatingContours = [];
    private bool _isUpdatingSeriesSelection;
    private int _currentSliceIndex;

    public StudyViewerWindow(string studyInstanceUid, IReadOnlyList<string> studyFilePaths)
    {
        InitializeComponent();

        StudyTitleTextBlock.Text = $"Study: {studyInstanceUid}";

        LoadStudy(studyFilePaths);
        SeriesSelectorComboBox.ItemsSource = _seriesGroups;
        RtStructListBox.ItemsSource = _rtStructItems;

        if (_seriesGroups.Count > 0)
        {
            SeriesSelectorComboBox.SelectedIndex = 0;
        }
        else
        {
            MessageBox.Show("表示可能な画像シリーズがありません。", "Study Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadStudy(IReadOnlyList<string> studyFilePaths)
    {
        var seriesLookup = new Dictionary<string, SeriesGroup>(StringComparer.Ordinal);

        foreach (var filePath in studyFilePaths)
        {
            try
            {
                var dicomFile = DicomFile.Open(filePath, FileReadOption.ReadLargeOnDemand);
                var dataset = dicomFile.Dataset;

                var modality = dataset.TryGetSingleValue(DicomTag.Modality, out string modalityValue)
                    ? modalityValue.ToUpperInvariant()
                    : "UNKNOWN";

                if (modality == "RTSTRUCT")
                {
                    ExtractRtStructContours(dataset);
                    continue;
                }

                if (!dataset.Contains(DicomTag.PixelData))
                {
                    continue;
                }

                var seriesInstanceUid = dataset.TryGetSingleValue(DicomTag.SeriesInstanceUID, out string seriesUid) && !string.IsNullOrWhiteSpace(seriesUid)
                    ? seriesUid
                    : filePath;

                if (!seriesLookup.TryGetValue(seriesInstanceUid, out var seriesGroup))
                {
                    seriesGroup = SeriesGroup.Create(filePath, seriesInstanceUid, modality, dataset);
                    seriesLookup[seriesInstanceUid] = seriesGroup;
                }

                seriesGroup.FilePaths.Add(filePath);
            }
            catch
            {
                // 読み取り失敗ファイルはスキップ
            }
        }

        _seriesGroups.Clear();
        _seriesGroups.AddRange(
            seriesLookup.Values
                .OrderBy(x => x.SortKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.DisplayText, StringComparer.OrdinalIgnoreCase));

        _rtStructItems.Sort((left, right) => string.Compare(left.DisplayText, right.DisplayText, StringComparison.OrdinalIgnoreCase));
    }

    private void SeriesSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSeriesSelection)
        {
            return;
        }

        if (SeriesSelectorComboBox.SelectedItem is not SeriesGroup selectedSeries)
        {
            return;
        }

        ApplySelectedSeries(selectedSeries);
    }

    private void ApplySelectedSeries(SeriesGroup selectedSeries)
    {
        _isUpdatingSeriesSelection = true;
        try
        {
            LoadSeriesImages(selectedSeries);
            RenderCurrentSlice();
        }
        finally
        {
            _isUpdatingSeriesSelection = false;
        }
    }

    private void LoadSeriesImages(SeriesGroup selectedSeries)
    {
        _imageSlices.Clear();
        _currentSliceIndex = 0;

        foreach (var filePath in selectedSeries.FilePaths)
        {
            try
            {
                var dicomFile = DicomFile.Open(filePath, FileReadOption.ReadLargeOnDemand);
                var dataset = dicomFile.Dataset;

                var modality = dataset.TryGetSingleValue(DicomTag.Modality, out string modalityValue)
                    ? modalityValue.ToUpperInvariant()
                    : "UNKNOWN";

                if (!dataset.Contains(DicomTag.PixelData) || modality == "RTSTRUCT")
                {
                    continue;
                }

                _imageSlices.Add(ImageSlice.Create(filePath, modality, dataset));
            }
            catch
            {
                // 読み取り失敗ファイルはスキップ
            }
        }

        _imageSlices.Sort((left, right) => left.SortKey.CompareTo(right.SortKey));
    }

    private void RtStructCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_imageSlices.Count == 0)
        {
            return;
        }

        RenderCurrentSlice();
    }

    private bool IsRtStructSelected(string rtStructKey)
    {
        if (_rtStructItemsByKey.TryGetValue(rtStructKey, out var item))
        {
            return item.IsSelected;
        }

        return true;
    }

    private RtStructDisplayItem GetOrCreateRtStructItem(string key)
    {
        if (_rtStructItemsByKey.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var created = new RtStructDisplayItem(key);
        _rtStructItemsByKey[key] = created;
        _rtStructItems.Add(created);
        return created;
    }

    private void ImageContainer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_imageSlices.Count == 0)
        {
            return;
        }

        _currentSliceIndex += e.Delta < 0 ? 1 : -1;

        if (_currentSliceIndex < 0)
        {
            _currentSliceIndex = 0;
        }

        if (_currentSliceIndex >= _imageSlices.Count)
        {
            _currentSliceIndex = _imageSlices.Count - 1;
        }

        RenderCurrentSlice();
        e.Handled = true;
    }

    private void ImageContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_imageSlices.Count == 0)
        {
            return;
        }

        var current = _imageSlices[_currentSliceIndex];
        var bitmap = current.GetBitmapSource();
        DrawContoursForCurrentSlice(current, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private void RenderCurrentSlice()
    {
        if (_imageSlices.Count == 0)
        {
            DicomImage.Source = null;
            ContourOverlay.Children.Clear();
            SliceInfoTextBlock.Text = "表示可能な画像スライスがありません。";
            return;
        }

        var current = _imageSlices[_currentSliceIndex];
        var bitmap = current.GetBitmapSource();
        DicomImage.Source = bitmap;

        SliceInfoTextBlock.Text = $"Slice {_currentSliceIndex + 1}/{_imageSlices.Count}  Modality: {current.Modality}  SOP: {current.SopInstanceUid}";

        DrawContoursForCurrentSlice(current, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private void DrawContoursForCurrentSlice(ImageSlice currentSlice, int sourceWidth, int sourceHeight)
    {
        ContourOverlay.Children.Clear();

        if (ImageContainer.ActualWidth <= 0 || ImageContainer.ActualHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(ImageContainer.ActualWidth / sourceWidth, ImageContainer.ActualHeight / sourceHeight);
        var offsetX = (ImageContainer.ActualWidth - (sourceWidth * scale)) / 2.0;
        var offsetY = (ImageContainer.ActualHeight - (sourceHeight * scale)) / 2.0;

        var contours = new List<ContourPolyline>();

        if (_contoursBySopInstanceUid.TryGetValue(currentSlice.SopInstanceUid, out var mappedContours))
        {
            contours.AddRange(mappedContours.Where(x => IsRtStructSelected(x.RtStructKey)));
        }

        contours.AddRange(_floatingContours.Where(x => IsRtStructSelected(x.RtStructKey) && IsOnCurrentSlice(x, currentSlice)));

        foreach (var contour in contours)
        {
            var polyline = new Polyline
            {
                Stroke = contour.Stroke,
                StrokeThickness = 1.5
            };

            foreach (var point in contour.PatientPoints)
            {
                if (!TryPatientPointToPixel(point, currentSlice, out var pixelX, out var pixelY))
                {
                    continue;
                }

                polyline.Points.Add(new Point(offsetX + (pixelX * scale), offsetY + (pixelY * scale)));
            }

            if (polyline.Points.Count >= 2)
            {
                ContourOverlay.Children.Add(polyline);
            }
        }
    }

    private static bool TryPatientPointToPixel(PatientPoint point, ImageSlice slice, out double pixelX, out double pixelY)
    {
        var dx = point.X - slice.ImagePositionPatient[0];
        var dy = point.Y - slice.ImagePositionPatient[1];
        var dz = point.Z - slice.ImagePositionPatient[2];

        var rowDirection = slice.RowDirection;
        var columnDirection = slice.ColumnDirection;

        pixelX = ((dx * rowDirection[0]) + (dy * rowDirection[1]) + (dz * rowDirection[2])) / slice.ColumnSpacing;
        pixelY = ((dx * columnDirection[0]) + (dy * columnDirection[1]) + (dz * columnDirection[2])) / slice.RowSpacing;

        return true;
    }

    private static bool IsOnCurrentSlice(ContourPolyline contour, ImageSlice slice)
    {
        var tolerance = Math.Max(1.0, slice.SliceThickness);

        foreach (var point in contour.PatientPoints)
        {
            var dx = point.X - slice.ImagePositionPatient[0];
            var dy = point.Y - slice.ImagePositionPatient[1];
            var dz = point.Z - slice.ImagePositionPatient[2];
            var distanceToPlane = Math.Abs((dx * slice.SliceNormal[0]) + (dy * slice.SliceNormal[1]) + (dz * slice.SliceNormal[2]));

            if (distanceToPlane <= tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private void ExtractRtStructContours(DicomDataset rtStructDataset)
    {
        var structureSetLabel = rtStructDataset.TryGetSingleValue(DicomTag.StructureSetLabel, out string label) && !string.IsNullOrWhiteSpace(label)
            ? label
            : "RTSTRUCT";

        var roiNameByNumber = new Dictionary<int, string>();
        if (rtStructDataset.TryGetSequence(DicomTag.StructureSetROISequence, out var structureSetRoiSequence))
        {
            foreach (var roiItem in structureSetRoiSequence.Items)
            {
                if (!roiItem.TryGetSingleValue(DicomTag.ROINumber, out int roiNumber))
                {
                    continue;
                }

                var roiName = roiItem.TryGetSingleValue(DicomTag.ROIName, out string name) && !string.IsNullOrWhiteSpace(name)
                    ? name
                    : $"ROI {roiNumber}";

                roiNameByNumber[roiNumber] = roiName;
            }
        }

        if (!rtStructDataset.TryGetSequence(DicomTag.ROIContourSequence, out var roiContourSequence))
        {
            _ = GetOrCreateRtStructItem(structureSetLabel);
            return;
        }

        foreach (var roiContour in roiContourSequence.Items)
        {
            var stroke = GetStrokeBrush(roiContour);
            var referencedRoiNumber = roiContour.TryGetSingleValue(DicomTag.ReferencedROINumber, out int roiNumber)
                ? roiNumber
                : -1;

            var roiName = referencedRoiNumber >= 0 && roiNameByNumber.TryGetValue(referencedRoiNumber, out var mappedName)
                ? mappedName
                : (referencedRoiNumber >= 0 ? $"ROI {referencedRoiNumber}" : "ROI");

            var rtStructKey = $"{structureSetLabel} / {roiName}";
            _ = GetOrCreateRtStructItem(rtStructKey);

            if (!roiContour.TryGetSequence(DicomTag.ContourSequence, out var contourSequence))
            {
                continue;
            }

            foreach (var contourItem in contourSequence.Items)
            {
                if (!contourItem.TryGetValues(DicomTag.ContourData, out double[] contourData) || contourData.Length < 6)
                {
                    continue;
                }

                var points = new List<PatientPoint>(contourData.Length / 3);
                for (var i = 0; i + 2 < contourData.Length; i += 3)
                {
                    points.Add(new PatientPoint(contourData[i], contourData[i + 1], contourData[i + 2]));
                }

                if (points.Count < 2)
                {
                    continue;
                }

                if (points[0] != points[^1])
                {
                    points.Add(points[0]);
                }

                var contour = new ContourPolyline(points, stroke, rtStructKey);
                var referencedSopUid = TryGetReferencedSopInstanceUid(contourItem);

                if (string.IsNullOrWhiteSpace(referencedSopUid))
                {
                    _floatingContours.Add(contour);
                    continue;
                }

                if (!_contoursBySopInstanceUid.TryGetValue(referencedSopUid, out var contours))
                {
                    contours = [];
                    _contoursBySopInstanceUid[referencedSopUid] = contours;
                }

                contours.Add(contour);
            }
        }
    }

    private static string? TryGetReferencedSopInstanceUid(DicomDataset contourItem)
    {
        if (!contourItem.TryGetSequence(DicomTag.ContourImageSequence, out var contourImageSequence))
        {
            return null;
        }

        foreach (var imageItem in contourImageSequence.Items)
        {
            if (imageItem.TryGetSingleValue(DicomTag.ReferencedSOPInstanceUID, out string sopUid) && !string.IsNullOrWhiteSpace(sopUid))
            {
                return sopUid;
            }
        }

        return null;
    }

    private static Brush GetStrokeBrush(DicomDataset roiContour)
    {
        if (roiContour.TryGetValues(DicomTag.ROIDisplayColor, out int[] rgb) && rgb.Length >= 3)
        {
            return new SolidColorBrush(Color.FromRgb((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]));
        }

        return Brushes.Lime;
    }

    private readonly record struct PatientPoint(double X, double Y, double Z);

    private sealed record ContourPolyline(IReadOnlyList<PatientPoint> PatientPoints, Brush Stroke, string RtStructKey);

    private sealed class RtStructDisplayItem
    {
        public RtStructDisplayItem(string displayText)
        {
            DisplayText = displayText;
            IsSelected = true;
        }

        public string DisplayText { get; }

        public bool IsSelected { get; set; }
    }

    private sealed class SeriesGroup
    {
        public SeriesGroup(string seriesInstanceUid, string modality, string? seriesDescription, int? seriesNumber)
        {
            SeriesInstanceUid = seriesInstanceUid;
            Modality = modality;
            SeriesDescription = seriesDescription;
            SeriesNumber = seriesNumber;
        }

        public string SeriesInstanceUid { get; }

        public string Modality { get; }

        public string? SeriesDescription { get; }

        public int? SeriesNumber { get; }

        public List<string> FilePaths { get; } = [];

        public string SortKey => SeriesNumber.HasValue
            ? $"{SeriesNumber.Value:D6}_{SeriesInstanceUid}"
            : SeriesInstanceUid;

        public string DisplayText
        {
            get
            {
                var parts = new List<string> { SeriesInstanceUid };
                if (SeriesNumber.HasValue)
                {
                    parts.Add($"No:{SeriesNumber.Value}");
                }

                parts.Add(Modality);

                if (!string.IsNullOrWhiteSpace(SeriesDescription))
                {
                    parts.Add(SeriesDescription);
                }

                parts.Add($"{FilePaths.Count} images");
                return string.Join(" | ", parts);
            }
        }

        public static SeriesGroup Create(string filePath, string seriesInstanceUid, string modality, DicomDataset dataset)
        {
            var seriesDescription = dataset.TryGetSingleValue(DicomTag.SeriesDescription, out string description) && !string.IsNullOrWhiteSpace(description)
                ? description
                : System.IO.Path.GetFileNameWithoutExtension(filePath);

            var seriesNumber = dataset.TryGetSingleValue(DicomTag.SeriesNumber, out int number)
                ? (int?)number
                : null;

            return new SeriesGroup(seriesInstanceUid, modality, seriesDescription, seriesNumber);
        }
    }

    private sealed class ImageSlice
    {
        private readonly string _filePath;
        private BitmapSource? _bitmap;

        private ImageSlice(
            string filePath,
            string modality,
            string sopInstanceUid,
            double[] imagePositionPatient,
            double[] rowDirection,
            double[] columnDirection,
            double[] sliceNormal,
            double rowSpacing,
            double columnSpacing,
            double sliceThickness,
            double sortKey)
        {
            _filePath = filePath;
            Modality = modality;
            SopInstanceUid = sopInstanceUid;
            ImagePositionPatient = imagePositionPatient;
            RowDirection = rowDirection;
            ColumnDirection = columnDirection;
            SliceNormal = sliceNormal;
            RowSpacing = rowSpacing;
            ColumnSpacing = columnSpacing;
            SliceThickness = sliceThickness;
            SortKey = sortKey;
        }

        public string Modality { get; }

        public string SopInstanceUid { get; }

        public double[] ImagePositionPatient { get; }

        public double[] RowDirection { get; }

        public double[] ColumnDirection { get; }

        public double[] SliceNormal { get; }

        public double RowSpacing { get; }

        public double ColumnSpacing { get; }

        public double SliceThickness { get; }

        public double SortKey { get; }

        public static ImageSlice Create(string filePath, string modality, DicomDataset dataset)
        {
            var sopInstanceUid = dataset.TryGetSingleValue(DicomTag.SOPInstanceUID, out string sopUid)
                ? sopUid
                : System.IO.Path.GetFileName(filePath);

            var imagePositionPatient = dataset.TryGetValues(DicomTag.ImagePositionPatient, out double[] ipp) && ipp.Length >= 3
                ? new double[] { ipp[0], ipp[1], ipp[2] }
                : new double[] { 0.0, 0.0, 0.0 };

            var imageOrientationPatient = dataset.TryGetValues(DicomTag.ImageOrientationPatient, out double[] iop) && iop.Length >= 6
                ? iop
                : new double[] { 1.0, 0.0, 0.0, 0.0, 1.0, 0.0 };

            var rowDirection = new[] { imageOrientationPatient[0], imageOrientationPatient[1], imageOrientationPatient[2] };
            var columnDirection = new[] { imageOrientationPatient[3], imageOrientationPatient[4], imageOrientationPatient[5] };
            var sliceNormal = new[]
            {
                (rowDirection[1] * columnDirection[2]) - (rowDirection[2] * columnDirection[1]),
                (rowDirection[2] * columnDirection[0]) - (rowDirection[0] * columnDirection[2]),
                (rowDirection[0] * columnDirection[1]) - (rowDirection[1] * columnDirection[0])
            };

            var pixelSpacing = dataset.TryGetValues(DicomTag.PixelSpacing, out double[] spacing) && spacing.Length >= 2
                ? spacing
                : new double[] { 1.0, 1.0 };

            var rowSpacing = pixelSpacing[0];
            var columnSpacing = pixelSpacing[1];

            var sliceThickness = dataset.TryGetSingleValue(DicomTag.SliceThickness, out double thickness)
                ? Math.Abs(thickness)
                : 1.0;

            var positionKey = (imagePositionPatient[0] * sliceNormal[0])
                + (imagePositionPatient[1] * sliceNormal[1])
                + (imagePositionPatient[2] * sliceNormal[2]);

            if (dataset.TryGetSingleValue(DicomTag.InstanceNumber, out int instanceNumber))
            {
                positionKey = (positionKey * 1000.0) + instanceNumber;
            }

            return new ImageSlice(
                filePath,
                modality,
                sopInstanceUid,
                imagePositionPatient,
                rowDirection,
                columnDirection,
                sliceNormal,
                rowSpacing,
                columnSpacing,
                sliceThickness,
                positionKey);
        }

        public BitmapSource GetBitmapSource()
        {
            if (_bitmap is not null)
            {
                return _bitmap;
            }

            var dicomImage = new DicomImage(_filePath);
            using var rendered = dicomImage.RenderImage().As<DrawingBitmap>();
            using var memoryStream = new MemoryStream();

            rendered.Save(memoryStream, DrawingImageFormat.Bmp);
            memoryStream.Position = 0;

            _bitmap = BitmapFrame.Create(memoryStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return _bitmap;
        }
    }
}
