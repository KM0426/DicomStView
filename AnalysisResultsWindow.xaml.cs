using System.Collections.Generic;
using System.Linq;
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

        public void DisplayHistogram(Dictionary<int, int> histogramData, string statistics)
        {
            HistogramChart.DataContext = histogramData;
            StatisticsTextBlock.Text = statistics;
        }
    }
}