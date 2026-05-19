using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls.DataVisualization.Charting;

namespace DicomStView
{
    public partial class AnalysisResultsWindow : Window
    {
        public AnalysisResultsWindow(IEnumerable<object> analysisResults)
        {
            InitializeComponent();
            ResultsDataGrid.ItemsSource = analysisResults;
        }

        public void DisplayHistogram(IEnumerable<HistogramPoint> histogramData, string statistics)
        {
            HistogramSeries.ItemsSource = histogramData;
            StatisticsTextBlock.Text = statistics;
        }

        public sealed record HistogramPoint(int X, int Y);
    }
}