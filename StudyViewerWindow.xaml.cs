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

namespace DicomStView
{
    public partial class StudyViewerWindow : Window
    {
        private readonly List<SeriesGroup> _seriesGroups = new();
        private readonly List<RtStructDisplayItem> _rtStructItems = new();
        private readonly Dictionary<string, RtStructDisplayItem> _rtStructItemsByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ImageSlice> _imageSlices = new();
        private readonly Dictionary<string, List<ContourPolyline>> _contoursBySopInstanceUid = new(StringComparer.Ordinal);
        private readonly List<ContourPolyline> _floatingContours = new();
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
                MessageBox.Show("No displayable series was found.", "Study Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    // Skip unreadable files.
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
                    // Skip unreadable files.
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
                SliceInfoTextBlock.Text = "No image slices are available.";
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
                    StrokeThickness = 1.5,
                    Tag = contour
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
                    polyline.MouseRightButtonDown += Polyline_MouseRightButtonDown;
                    ContourOverlay.Children.Add(polyline);
                }
            }
        }

        private void Polyline_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Polyline polyline || polyline.Tag is not ContourPolyline contour)
            {
                return;
            }

            var contextMenu = new ContextMenu();

            var roiNameItem = new MenuItem
            {
                Header = $"ROI: {contour.RtStructKey}",
                IsEnabled = false
            };
            contextMenu.Items.Add(roiNameItem);
            contextMenu.Items.Add(new Separator());

            var analyzeHeaderItem = new MenuItem
            {
                Header = "Analyze",
                IsEnabled = false
            };
            contextMenu.Items.Add(analyzeHeaderItem);

            var circleEvalItem = new MenuItem
            {
                Header = "Circle Metrics"
            };
            circleEvalItem.Click += CircleEvalItem_Click;
            circleEvalItem.Tag = contour;
            contextMenu.Items.Add(circleEvalItem);

            var sphereEvalItem = new MenuItem
            {
                Header = "Sphere Metrics"
            };
            sphereEvalItem.Click += SphereEvalItem_Click;
            sphereEvalItem.Tag = contour;
            contextMenu.Items.Add(sphereEvalItem);

            var ctHistogramItem = new MenuItem
            {
                Header = "CT Value Histogram"
            };
            ctHistogramItem.Click += CtValueHistogramItem_Click;
            ctHistogramItem.Tag = contour;
            contextMenu.Items.Add(ctHistogramItem);

            polyline.ContextMenu = contextMenu;
            contextMenu.IsOpen = true;

            e.Handled = true;
        }

        private void CircleEvalItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem || menuItem.Tag is not ContourPolyline selectedContour)
            {
                return;
            }

            var targetContours = GetContoursByRtStructKey(selectedContour.RtStructKey);
            if (targetContours.Count == 0)
            {
                MessageBox.Show("選択された輪郭が見つかりません。処理を続行できません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var perSlice = BuildPerSliceCircleMetrics(targetContours);
            if (perSlice.Count == 0)
            {
                MessageBox.Show("選択された輪郭が見つかりません。処理を続行できません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var results = perSlice.Select(x => new
            {
                Z = x.Z,
                Circularity = x.Circularity,
                AspectRatio = x.AspectRatio,
                Roundness = x.Roundness
            }).ToList();

            ShowAnalysisResults(results);
        }

        private void SphereEvalItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem || menuItem.Tag is not ContourPolyline selectedContour)
            {
                return;
            }

            var targetContours = GetContoursByRtStructKey(selectedContour.RtStructKey);
            if (targetContours.Count < 2)
            {
                MessageBox.Show("選択された輪郭が2つ未満です。処理を続行できません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sliceProfiles = BuildPerSliceProfiles(targetContours)
                .OrderBy(x => x.Z)
                .ToList();

            if (sliceProfiles.Count < 2)
            {
                MessageBox.Show("スライスプロファイルが2つ未満です。処理を続行できません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var volume = 0.0;
            var sideSurface = 0.0;

            for (var i = 0; i + 1 < sliceProfiles.Count; i++)
            {
                var current = sliceProfiles[i];
                var next = sliceProfiles[i + 1];
                var dz = Math.Abs(next.Z - current.Z);

                if (dz <= 0)
                {
                    continue;
                }

                volume += (dz / 3.0) * (current.Area + next.Area + Math.Sqrt(current.Area * next.Area));
                sideSurface += 0.5 * (current.Perimeter + next.Perimeter) * dz;
            }

            var capSurface = sliceProfiles[0].Area + sliceProfiles[^1].Area;
            var objectSurface = sideSurface + capSurface;

            if (volume <= 0 || objectSurface <= 0)
            {
                MessageBox.Show("Volume or surface area is zero.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var sphereSurface = Math.Pow(Math.PI, 1.0 / 3.0) * Math.Pow(6.0 * volume, 2.0 / 3.0);
            var sphericity = sphereSurface / objectSurface;

            var results = new List<object>
            {
                new
                {
                    ROI = selectedContour.RtStructKey,
                    Volume = volume,
                    SurfaceArea = objectSurface,
                    SphereSurfaceArea = sphereSurface,
                    Sphericity = sphericity
                }
            };

            ShowAnalysisResults(results);
        }

        private void CtValueHistogramItem_Click(object sender, RoutedEventArgs e)
        {
            if (_imageSlices.Count == 0)
            {
                MessageBox.Show("No image slice is loaded.", "CT Histogram", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!TryBuildCtHistogramForCurrentSlice(out var histogram, out var statistics))
            {
                MessageBox.Show("Failed to compute CT histogram for the current slice.", "CT Histogram", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var window = new AnalysisResultsWindow(new[]
            {
                new
                {
                    Slice = _currentSliceIndex + 1,
                    Bins = histogram.Count,
                    VoxelCount = histogram.Values.Sum()
                }
            });

            window.DisplayHistogram(histogram, statistics);
            window.Owner = this;
            window.Show();
        }

        private bool TryBuildCtHistogramForCurrentSlice(out Dictionary<int, int> histogram, out string statistics)
        {
            histogram = new Dictionary<int, int>();
            statistics = string.Empty;

            var current = _imageSlices[_currentSliceIndex];

            try
            {
                var dicomFile = DicomFile.Open(current.FilePath, FileReadOption.ReadLargeOnDemand);
                var dataset = dicomFile.Dataset;

                if (!dataset.Contains(DicomTag.PixelData))
                {
                    return false;
                }

                var pixelData = DicomPixelData.Create(dataset);
                if (pixelData.NumberOfFrames == 0)
                {
                    return false;
                }

                var slope = dataset.TryGetSingleValue(DicomTag.RescaleSlope, out double slopeValue) ? slopeValue : 1.0;
                var intercept = dataset.TryGetSingleValue(DicomTag.RescaleIntercept, out double interceptValue) ? interceptValue : 0.0;

                var frameBytes = pixelData.GetFrame(0).Data;
                var values = new List<double>(frameBytes.Length / Math.Max(1, pixelData.BytesAllocated));

                if (pixelData.BitsAllocated <= 8)
                {
                    for (var i = 0; i < frameBytes.Length; i++)
                    {
                        var raw = pixelData.PixelRepresentation == PixelRepresentation.Signed
                            ? (double)unchecked((sbyte)frameBytes[i])
                            : frameBytes[i];
                        values.Add((raw * slope) + intercept);
                    }
                }
                else
                {
                    for (var i = 0; i + 1 < frameBytes.Length; i += 2)
                    {
                        var raw = pixelData.PixelRepresentation == PixelRepresentation.Signed
                            ? (double)BitConverter.ToInt16(frameBytes, i)
                            : BitConverter.ToUInt16(frameBytes, i);
                        values.Add((raw * slope) + intercept);
                    }
                }

                if (values.Count == 0)
                {
                    return false;
                }

                const int binWidth = 25;
                foreach (var value in values)
                {
                    var binCenter = (int)(Math.Round(value / binWidth) * binWidth);
                    histogram.TryGetValue(binCenter, out var count);
                    histogram[binCenter] = count + 1;
                }

                statistics = string.Join(
                    Environment.NewLine,
                    $"Min HU: {values.Min():F1}",
                    $"Max HU: {values.Max():F1}",
                    $"Mean HU: {Average(values):F1}",
                    $"Median HU: {Median(values):F1}",
                    $"Samples: {values.Count}");

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ShowAnalysisResults(object results)
        {
            var rows = results switch
            {
                IEnumerable<object> many => many,
                _ => new[] { results }
            };

            var window = new AnalysisResultsWindow(rows)
            {
                Owner = this
            };

            window.Show();
        }

        private List<ContourPolyline> GetContoursByRtStructKey(string rtStructKey)
        {
            var allContours = _contoursBySopInstanceUid.Values.SelectMany(x => x).Concat(_floatingContours);
            return allContours.Where(x => string.Equals(x.RtStructKey, rtStructKey, StringComparison.Ordinal)).ToList();
        }

        private static List<SliceCircleMetrics> BuildPerSliceCircleMetrics(IReadOnlyList<ContourPolyline> contours)
        {
            var profiles = BuildPerSliceProfiles(contours);
            var metrics = new List<SliceCircleMetrics>();

            foreach (var profile in profiles)
            {
                var perimeterSquared = profile.Perimeter * profile.Perimeter;
                if (profile.Area <= 0 || perimeterSquared <= 0 || profile.MajorLength <= 0)
                {
                    continue;
                }

                var circularity = (4.0 * Math.PI * profile.Area) / perimeterSquared;
                var aspectRatio = profile.MinorLength / profile.MajorLength;
                var roundness = (4.0 * profile.Area) / (Math.PI * profile.MajorLength * profile.MajorLength);

                metrics.Add(new SliceCircleMetrics(profile.Z, circularity, aspectRatio, roundness));
            }

            return metrics;
        }

        private static List<SliceProfile> BuildPerSliceProfiles(IReadOnlyList<ContourPolyline> contours)
        {
            var contourMetrics = new List<ContourMetric>();

            foreach (var contour in contours)
            {
                var metric = TryCalculateContourMetric(contour.PatientPoints);
                if (metric is not null)
                {
                    contourMetrics.Add(metric.Value);
                }
            }

            return contourMetrics
                .GroupBy(x => Math.Round(x.Z, 2))
                .Select(g =>
                {
                    var totalArea = g.Sum(x => x.Area);
                    var totalPerimeter = g.Sum(x => x.Perimeter);
                    var major = g.Max(x => x.MajorLength);
                    var minor = g.Max(x => x.MinorLength);
                    return new SliceProfile(g.Key, totalArea, totalPerimeter, major, minor);
                })
                .OrderBy(x => x.Z)
                .ToList();
        }

        private static ContourMetric? TryCalculateContourMetric(IReadOnlyList<PatientPoint> points)
        {
            if (points.Count < 3)
            {
                return null;
            }

            var first = points[0];
            var second = points[1];
            var third = points[2];

            var ux = second.X - first.X;
            var uy = second.Y - first.Y;
            var uz = second.Z - first.Z;
            var uNorm = Math.Sqrt((ux * ux) + (uy * uy) + (uz * uz));
            if (uNorm <= 0)
            {
                return null;
            }

            ux /= uNorm;
            uy /= uNorm;
            uz /= uNorm;

            var wx = third.X - first.X;
            var wy = third.Y - first.Y;
            var wz = third.Z - first.Z;

            var nx = (uy * wz) - (uz * wy);
            var ny = (uz * wx) - (ux * wz);
            var nz = (ux * wy) - (uy * wx);
            var nNorm = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
            if (nNorm <= 0)
            {
                return null;
            }

            nx /= nNorm;
            ny /= nNorm;
            nz /= nNorm;

            var vx = (ny * uz) - (nz * uy);
            var vy = (nz * ux) - (nx * uz);
            var vz = (nx * uy) - (ny * ux);

            var projected = new List<(double X, double Y)>(points.Count);
            foreach (var p in points)
            {
                var dx = p.X - first.X;
                var dy = p.Y - first.Y;
                var dz = p.Z - first.Z;
                projected.Add(((dx * ux) + (dy * uy) + (dz * uz), (dx * vx) + (dy * vy) + (dz * vz)));
            }

            var area = 0.0;
            var perimeter = 0.0;
            var minX = double.MaxValue;
            var maxX = double.MinValue;
            var minY = double.MaxValue;
            var maxY = double.MinValue;

            for (var i = 0; i < projected.Count; i++)
            {
                var current = projected[i];
                var next = projected[(i + 1) % projected.Count];
                area += (current.X * next.Y) - (next.X * current.Y);

                var dx = next.X - current.X;
                var dy = next.Y - current.Y;
                perimeter += Math.Sqrt((dx * dx) + (dy * dy));

                if (current.X < minX) minX = current.X;
                if (current.X > maxX) maxX = current.X;
                if (current.Y < minY) minY = current.Y;
                if (current.Y > maxY) maxY = current.Y;
            }

            area = Math.Abs(area) * 0.5;
            var width = Math.Max(0.0, maxX - minX);
            var height = Math.Max(0.0, maxY - minY);
            var major = Math.Max(width, height);
            var minor = Math.Min(width, height);
            var avgZ = points.Average(x => x.Z);

            if (area <= 0 || perimeter <= 0 || major <= 0)
            {
                return null;
            }

            return new ContourMetric(avgZ, area, perimeter, major, Math.Max(1e-9, minor));
        }

        private static double Average(IReadOnlyList<double> values)
        {
            return values.Count == 0 ? 0.0 : values.Average();
        }

        private static double Median(IReadOnlyList<double> values)
        {
            if (values.Count == 0)
            {
                return 0.0;
            }

            var sorted = values.OrderBy(x => x).ToList();
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 0
                ? (sorted[mid - 1] + sorted[mid]) / 2.0
                : sorted[mid];
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

        internal readonly record struct PatientPoint(double X, double Y, double Z);

        private readonly record struct ContourMetric(double Z, double Area, double Perimeter, double MajorLength, double MinorLength);

        private readonly record struct SliceProfile(double Z, double Area, double Perimeter, double MajorLength, double MinorLength);

        private readonly record struct SliceCircleMetrics(double Z, double Circularity, double AspectRatio, double Roundness);

        internal sealed record ContourPolyline(IReadOnlyList<PatientPoint> PatientPoints, Brush Stroke, string RtStructKey);

        internal sealed class RtStructDisplayItem
        {
            public RtStructDisplayItem(string displayText)
            {
                DisplayText = displayText;
                IsSelected = true;
            }

            public string DisplayText { get; }

            public bool IsSelected { get; set; }
        }

        internal sealed class SeriesGroup
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

        internal sealed class ImageSlice
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

            public string FilePath => _filePath;

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
}

