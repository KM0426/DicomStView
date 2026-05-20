using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DicomStView
{
    public partial class AnalysisResultsWindow : Window
    {
        private const string CircleResultTypeName = "CircleAnalysisResultRow";
        private const string SphereResultTypeName = "SphereAnalysisResultRow";

        private static readonly Dictionary<string, (string Header, int Order)> CircleColumns = new(StringComparer.Ordinal)
        {
            ["Z"] = ("Z", 0),
            ["Samples"] = ("Samples", 1),
            ["AreaMm2"] = ("Area(mm2)", 2),
            ["MinHU"] = ("Min(HU)", 3),
            ["MaxHU"] = ("Max(HU)", 4),
            ["MeanHU"] = ("Mean(HU)", 5),
            ["MedianHU"] = ("Median(HU)", 6),
            ["SD"] = ("SD", 7),
            ["CVPercent"] = ("CV(%)", 8),
            ["RMS"] = ("RMS", 9),
            ["Circularity"] = ("Circularity", 10),
            ["AspectRatio"] = ("AspectRatio", 11),
            ["Roundness"] = ("Roundness", 12)
        };

        private static readonly Dictionary<string, (string Header, int Order)> SphereColumns = new(StringComparer.Ordinal)
        {
            ["ROI"] = ("ROI", 0),
            ["Samples"] = ("Samples", 1),
            ["Volume"] = ("Volume(mm3)", 2),
            ["MinHU"] = ("Min(HU)", 3),
            ["MaxHU"] = ("Max(HU)", 4),
            ["MeanHU"] = ("Mean(HU)", 5),
            ["MedianHU"] = ("Median(HU)", 6),
            ["SD"] = ("SD", 7),
            ["CVPercent"] = ("CV(%)", 8),
            ["RMS"] = ("RMS", 9),
            ["SurfaceArea"] = ("SurfaceArea(mm2)", 10),
            ["SphereSurfaceArea"] = ("SphereSurfaceArea(mm2)", 11),
            ["Sphericity"] = ("Sphericity", 12)
        };

        private Dictionary<string, HistogramPayload>? _selectableHistograms;
        private Func<object, string?>? _histogramKeySelector;
        private readonly string? _rowTypeName;

        public AnalysisResultsWindow(IEnumerable<object> analysisResults)
        {
            var rows = analysisResults.ToList();

            InitializeComponent();
            ResultsDataGrid.ItemsSource = rows;
            _rowTypeName = rows.FirstOrDefault()?.GetType().Name;
            ResultsDataGrid.AutoGeneratingColumn += ResultsDataGrid_AutoGeneratingColumn;
            ResultsDataGrid.SelectionChanged += ResultsDataGrid_SelectionChanged;
        }

        public void DisplayHistogram(IEnumerable<HistogramPoint> histogramData, string statistics)
        {
            HistogramSeries.ItemsSource = histogramData;
        }

        public void ConfigureSelectableHistograms(
            IReadOnlyDictionary<string, HistogramPayload> histograms,
            Func<object, string?> histogramKeySelector,
            string? initialSelectedKey = null)
        {
            _selectableHistograms = histograms.ToDictionary(x => x.Key, x => x.Value);
            _histogramKeySelector = histogramKeySelector;

            if (!string.IsNullOrWhiteSpace(initialSelectedKey))
            {
                foreach (var item in ResultsDataGrid.Items)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    if (histogramKeySelector(item) != initialSelectedKey)
                    {
                        continue;
                    }

                    ResultsDataGrid.SelectedItem = item;
                    ResultsDataGrid.ScrollIntoView(item);
                    return;
                }
            }

            if (ResultsDataGrid.SelectedItem is not null)
            {
                UpdateHistogramForSelection(ResultsDataGrid.SelectedItem);
                return;
            }

            if (ResultsDataGrid.Items.Count > 0)
            {
                ResultsDataGrid.SelectedIndex = 0;
            }
        }

        private void ResultsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultsDataGrid.SelectedItem is null)
            {
                return;
            }

            UpdateHistogramForSelection(ResultsDataGrid.SelectedItem);
        }

        private void ResultsDataGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            var map = _rowTypeName switch
            {
                CircleResultTypeName => CircleColumns,
                SphereResultTypeName => SphereColumns,
                _ => null
            };

            if (map is null)
            {
                return;
            }

            if (map.TryGetValue(e.PropertyName, out var config))
            {
                e.Column.Header = config.Header;
                e.Column.DisplayIndex = config.Order;
            }
        }

        private void UpdateHistogramForSelection(object selectedItem)
        {
            if (_selectableHistograms is null || _histogramKeySelector is null)
            {
                return;
            }

            var key = _histogramKeySelector(selectedItem);
            if (key is null || !_selectableHistograms.TryGetValue(key, out var payload))
            {
                return;
            }

            DisplayHistogram(payload.Histogram, payload.Description);
        }

        public sealed record HistogramPoint(int X, int Y);

        public sealed record HistogramStatistics(
            int Samples,
            double MinHU,
            double MaxHU,
            double MeanHU,
            double MedianHU,
            double SD,
            double CVPercent,
            double RMS);

        public sealed record HistogramPayload(
            IReadOnlyList<HistogramPoint> Histogram,
            HistogramStatistics Statistics,
            string Description);
    }
}