using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using TalisScraper.Events.Args;
using TalisScraper.Interfaces;

namespace TalisScrapeWPF.Pages.Scrape
{
    public partial class Reports
    {
        private readonly IScraper _scraper;
        
        public Reports()
        {
            _scraper = App.Container.Resolve<IScraper>();


            InitializeComponent();

            var reports = _scraper.FetchAllScrapeReports();

            LvReports.ItemsSource = reports;
        }

        private void LvReports_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
           // throw new System.NotImplementedException();
        }
    }
}
