﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TalisScraper.Objects;
using Cache;
using Extensions;
using Newtonsoft.Json.Linq;
using NLog;
using TalisScraper.Enums;
using TalisScraper.Events.Args;
using TalisScraper.Interfaces;
using TalisScraper.Objects.JsonMaps;

//TODO: should we lock per scrape, so if another scrape is initiated before current scrap[e ends, we deny it? Also a 'stop scrape' function?

//Make internals visible to testing framework
#if DEBUG
[assembly: InternalsVisibleTo("TalisScrapeTest.Tests")]
#endif
namespace TalisScraper
{
    //TODO: have a scrape options class for configuration?
    public class JsonScraper : IScraper
    {
        private const string RootRegex = "\"([^\"]+)\"";
        private readonly IRequestHandler _requestHandler;

        private readonly ScrapeConfig _scrapeConfig;

        private volatile bool _scrapeCancelled;

        private ScrapeReport _scrapeReport = null;

        public JsonScraper(IRequestHandler requestHandler, ScrapeConfig scrapeConfig = null)
        {
            Log = LogManager.GetCurrentClassLogger();//todo: inject this in?
            _requestHandler = requestHandler;
            _scrapeConfig = scrapeConfig;
        }

        public ILogger Log { get; set; }
        public ICache Cache { get; set; }
        
        #region Async Functions
        public event EventHandler<ScrapeEndedEventArgs> ScrapeEnded;
        public event EventHandler<ScrapeCancelledEventArgs> ScrapeCancelled;
        public event EventHandler<ScrapeStartedEventArgs> ScrapeStarted;
        public event EventHandler<ResourceScrapedEventArgs> ResourceScraped;

        /// <summary>
        /// Fetches json object from the specified uri using async
        /// </summary>
        /// <param name="uri">uri of json object</param>
        /// <returns>a string json object</returns>
        internal async Task<string> FetchJsonAsync(string uri)
        {
            var json = await _requestHandler.FetchJsonAsync(uri).ConfigureAwait(false);

            if (string.IsNullOrEmpty(json) && _scrapeReport != null)
                _scrapeReport.FailedScrapes.Add(uri);

            if (_scrapeReport != null)
                _scrapeReport.TotalRequestsMade++;

            if (ResourceScraped != null) ResourceScraped(this, new ResourceScrapedEventArgs(uri));

            return json;
        }

        internal async Task<NavItem> FetchItemsInternalAsync(string uri)
        {
            var basObj = Cache.FetchItem<NavItem>(uri);

            if (basObj == null)
            {

                var json = await FetchJsonAsync(uri).ConfigureAwait(false);


                basObj = NavItemParser(json);

                if (basObj != null)
                {
                    Cache.PutItem(basObj, uri);
                }
            }
            else
            {
                if (_scrapeReport != null)
                    _scrapeReport.TotalCacheRequestsMade++;
            }

            return basObj;
        }

        public async Task<NavItem> FetchNavItemAsync(string uri)
        {
            _scrapeCancelled = false;
            if (ScrapeStarted != null) ScrapeStarted(this, new ScrapeStartedEventArgs(ScrapeType.ReadingList));
            var items = await FetchItemsInternalAsync(uri).ConfigureAwait(false);
            if (ScrapeEnded != null) ScrapeEnded(this, new ScrapeEndedEventArgs(ScrapeType.ReadingList));

            return items;
        }

        private async Task RecParseAsync(string loc, List<string> list)
        {
            if (_scrapeCancelled)
                return;

           // await Task.Yield();
            var items = await FetchItemsInternalAsync(loc).ConfigureAwait(false);

            if (items != null)
            {
                foreach (var ou in items.Items.OrganizationalUnit ?? new Element[] {})
                {
                    await RecParseAsync(string.Format("{0}.json", ou.Value), list).ConfigureAwait(false);
                }

                foreach (var ou in items.Items.KnowledgeGrouping ?? new Element[] { })
                {
                    await RecParseAsync(string.Format("{0}.json", ou.Value), list).ConfigureAwait(false);
                }

                if (items.Items.UsesList.HasContent())
                {
                    list.AddRange(items.Items.UsesList.Select(n => n.Value));
                }
            }
        }

        private async Task RecParseParallelAsync(string loc, ConcurrentBag<string> list)
        {
            if (_scrapeCancelled)
                return;

            var items = await FetchItemsInternalAsync(loc).ConfigureAwait(false);

            if (items != null)
            {
                if (items.Items.OrganizationalUnit.HasContent())
                {
                    Parallel.ForEach(items.Items.OrganizationalUnit, async (item, state) =>
                    {
                        await RecParseParallelAsync(string.Format("{0}.json", item.Value), list).ConfigureAwait(false);
                    });
                }

                if (items.Items.KnowledgeGrouping.HasContent())
                {
                    Parallel.ForEach(items.Items.KnowledgeGrouping, async (item, state) =>
                    {
                        await RecParseParallelAsync(string.Format("{0}.json", item.Value), list).ConfigureAwait(false);
                    });
                }

                if (items.Items.UsesList.HasContent())
                {
                    Parallel.ForEach(items.Items.UsesList, (item, state) =>
                    {
                        list.Add(item.Value);
                    });
                }
            }
        }

        public async Task<IEnumerable<string>> ScrapeReadingListsAsync(string root)
        {
            if (string.IsNullOrEmpty(root))
            {
                Log.Error("Scraper.ParseTest: Could not initiate scrape. The root node address was empty.");
                return null;
            }
            _scrapeCancelled = false;
            var doParallel = _scrapeConfig != null && _scrapeConfig.EnableParallelProcessing;

            var lists = new List<string>();
            var stopwatch = new Stopwatch();
            _scrapeReport = new ScrapeReport();

            if (ScrapeStarted != null) ScrapeStarted(this, new ScrapeStartedEventArgs(ScrapeType.ReadingList));

            _scrapeReport.ScrapeStarted = DateTime.Now;

            stopwatch.Start();

            if (doParallel)
            {
                var listsP = new ConcurrentBag<string>();

                await RecParseParallelAsync(root, listsP).ConfigureAwait(false);

                lists = listsP.ToList();
            }
            else
            {
                await RecParseAsync(root, lists).ConfigureAwait(false);
            }

            stopwatch.Stop();

            _scrapeReport.ScrapeEnded = DateTime.Now;
            _scrapeReport.TimeTaken = stopwatch.Elapsed;

            if (ScrapeEnded != null) ScrapeEnded(this, new ScrapeEndedEventArgs(ScrapeType.ReadingList));

            return lists;
        }
        #endregion

        #region Sync Functions
        /// <summary>
        /// Fetches json object from the specified uri
        /// </summary>
        /// <param name="uri">uri of json object</param>
        /// <returns>a string json object</returns>
        internal string FetchJson(string uri)
        {
            var json = _requestHandler.FetchJson(uri);

            if (string.IsNullOrEmpty(json) && _scrapeReport != null)
                _scrapeReport.FailedScrapes.Add(uri);

            if (_scrapeReport != null)
                _scrapeReport.TotalRequestsMade++;

            if (ResourceScraped != null) ResourceScraped(this, new ResourceScrapedEventArgs(uri));

            return json;
        }


        private NavItem NavItemParser(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;

            var replaceRootRegex = new Regex(RootRegex);

            var finalJson = replaceRootRegex.Replace(json, "\"root\"", 1);
            NavItem convertedNav = null;

            try
            {
                convertedNav = JsonConvert.DeserializeObject<NavItem>(finalJson);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }

            return convertedNav;
        }


        /// <summary>
        /// Fetches Items, has been made internal so the public Fetch Item func can fire scrape start and stop events for individual items
        /// </summary>
        internal NavItem FetchItemsInternal(string uri)
        {
            var basObj = Cache.FetchItem<NavItem>(uri);

            if (basObj == null)
            {
                var json = FetchJson(uri);

                basObj = NavItemParser(json);

                if (basObj != null)
                {
                    Cache.PutItem(basObj, uri);
                }

            }
            else
            {
                if (_scrapeReport != null)
                    _scrapeReport.TotalCacheRequestsMade++;
            }

            return basObj;
        }

        public NavItem FetchNavItem(string uri)
        {
            _scrapeCancelled = false;
            if (ScrapeStarted != null) ScrapeStarted(this, new ScrapeStartedEventArgs(ScrapeType.ReadingList));
            var items = FetchItemsInternal(uri);
            if (ScrapeEnded != null) ScrapeEnded(this, new ScrapeEndedEventArgs(ScrapeType.ReadingList));

            return items;
        }

        private void RecParse(string loc, ref List<string> list)
        {
            if (_scrapeCancelled)
                return;

            var items = FetchItemsInternal(loc);

            if (items != null)
            {
                foreach (var ou in items.Items.OrganizationalUnit ?? new Element[] { })
                {
                    RecParse(string.Format("{0}.json", ou.Value), ref list);
                }

                foreach (var ou in items.Items.KnowledgeGrouping ?? new Element[] { })
                {
                    RecParse(string.Format("{0}.json", ou.Value), ref list);
                }

                if (items.Items.UsesList.HasContent())
                {
                    list.AddRange(items.Items.UsesList.Select(n =>  n.Value));
                }
            }

        }

        private void RecParseParallel(string loc, ConcurrentBag<string> list)
        {
            if (_scrapeCancelled)
                return;

            var items = FetchItemsInternal(loc);

            if (items != null)
            {
                if (items.Items.OrganizationalUnit.HasContent())
                {
                    Parallel.ForEach(items.Items.OrganizationalUnit, (item, status) =>
                    {
                        RecParseParallel(string.Format("{0}.json", item.Value), list);
                    });
                }

                if (items.Items.KnowledgeGrouping.HasContent())
                {
                    Parallel.ForEach(items.Items.KnowledgeGrouping, (item, status) =>
                    {
                        RecParseParallel(string.Format("{0}.json", item.Value), list);
                    });
                }

                if (items.Items.UsesList.HasContent())
                {
                    Parallel.ForEach(items.Items.UsesList, (item, status) =>
                    {
                        list.Add(item.Value);
                    });
                    
                }
            }

        }

        public IEnumerable<string> ScrapeReadingLists(string root)
        {
            if (string.IsNullOrEmpty(root))
            {
                Log.Fatal("Scraper.ParseTest: Could not initiate scrape. The root node address was empty.");
                return null;
            }

            _scrapeCancelled = false;
            var lists = new List<string>();
            var stopwatch = new Stopwatch();
            _scrapeReport = new ScrapeReport();

            if (ScrapeStarted != null) ScrapeStarted(this, new ScrapeStartedEventArgs(ScrapeType.ReadingList));

            _scrapeReport.ScrapeStarted = DateTime.Now;

            stopwatch.Start();

            if (_scrapeConfig != null && _scrapeConfig.EnableParallelProcessing)
            {
                var listP = new ConcurrentBag<string>();
                RecParseParallel(root, listP);

                lists = listP.ToList();
            }
            else
            {
                RecParse(root, ref lists);
            }

            stopwatch.Stop();

            _scrapeReport.ScrapeEnded = DateTime.Now;
            _scrapeReport.TimeTaken = stopwatch.Elapsed;

            if (ScrapeEnded != null) ScrapeEnded(this, new ScrapeEndedEventArgs(ScrapeType.ReadingList));

            return lists;
        }


        private IEnumerable<ReadingList> DoPopulateReadingListsParallel(IEnumerable<string> readingLists)
        {
            var readingListCollection = new ConcurrentBag<ReadingList>();

            Parallel.ForEach(readingLists, (currentUri, state) =>
            {
                if (_scrapeCancelled) state.Break();
                var uri = string.Format("{0}.json", currentUri);

                var readingListNavObj = FetchItemsInternal(uri);

                if (readingListNavObj == null)
                {
                    Log.Error("Reading list from uri:{0} could not be scraped.", uri);

                }
                else
                {
                    readingListCollection.Add(new ReadingList { Uri = uri, ListInfo = readingListNavObj });
                }

                
            });

            var tst = readingListCollection;//.ToArray();

            if (tst.HasContent())
            {//fetch books from discovered lists and add them to the relevant list

                Parallel.ForEach(readingListCollection, (currentList, state) =>
                {
                    if (_scrapeCancelled) state.Break();
                    foreach (var rlItemList in currentList.ListInfo.Items.Contains)
                    {
                        if (_scrapeCancelled) state.Break();
                        if (rlItemList.Value.Contains("/items/"))
                        {
                            var getbookItems = FetchItemsInternal(currentList.Uri);

                            if (getbookItems != null && getbookItems.Items.Contains.HasContent())
                            {//scrape individual book info

                                Parallel.ForEach(getbookItems.Items.Contains, (item, subState) =>
                                {
                                    {
                                        if (_scrapeCancelled) state.Break();
                                        if (item.Value.Contains("/items/"))
                                        {
                                            var bookItem = FetchJson(string.Format("{0}.json", item.Value));

                                            if (!string.IsNullOrEmpty(bookItem))
                                            {
                                                var bookObj = ParseBookInfoFromJson(item.Value, bookItem);

                                                if (bookObj != null)
                                                    currentList.Books.Add(bookObj);
                                            }
                                        }
                                    }
                                });
                            }
                        }
                    }


                });


            }

            return readingListCollection.ToList();
        }

        private IEnumerable<ReadingList> DoPopulateReadingLists(IEnumerable<string> readingLists)
        {
            var readingListCollection = new Collection<ReadingList>();
            foreach (var item in readingLists)
            {
                var uri = string.Format("{0}.json", item);

                var readingListNavObj = FetchItemsInternal(uri);

                if (readingListNavObj == null)
                {
                    Log.Error("Reading list from uri:{0} could not be scraped.", uri);
                    continue;
                }

                readingListCollection.Add(new ReadingList { Uri = uri, ListInfo = readingListNavObj });

            }

            if (readingListCollection.HasContent())
            {//fetch books from discovered lists and add them to the relevant list
                foreach (var rlItem in readingListCollection)
                {
                    if (_scrapeCancelled) return null;
                    foreach (var rlItemList in rlItem.ListInfo.Items.Contains)
                    {
                        if (_scrapeCancelled) return null;
                        if (rlItemList.Value.Contains("/items/"))
                        {
                            var getbookItems = FetchItemsInternal(rlItem.Uri);

                            if (getbookItems != null && getbookItems.Items.Contains.HasContent())
                            {//scrape individual book info
                                foreach (var book in getbookItems.Items.Contains)
                                {
                                    if (_scrapeCancelled) return null;
                                    if (book.Value.Contains("/items/"))
                                    {
                                        var bookItem = FetchJson(string.Format("{0}.json", book.Value));

                                        if (!string.IsNullOrEmpty(bookItem))
                                        {
                                            var bookObj = ParseBookInfoFromJson(book.Value, bookItem);

                                            if (bookObj != null)
                                                rlItem.Books.Add(bookObj);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return readingListCollection;
        }

        //todo: how does cancel scrape fit into this? Might pass a prescraped collection in, so can't assume we outright cancel it
        public IEnumerable<ReadingList> PopulateReadingLists(IEnumerable<string> readingLists)
        {//scrape lists from passed in uri collection of lists
            _scrapeCancelled = false;
            if (!readingLists.HasContent())
            {
                Log.Error("Attempted to populate reading lists, but passed in list object was null.");
                return null;
            }


            if (ScrapeStarted != null) ScrapeStarted(this, new ScrapeStartedEventArgs(ScrapeType.Books));
            _scrapeReport = new ScrapeReport {ScrapeStarted = DateTime.Now};

            IEnumerable<ReadingList> readingListsFinal;

            var stopWatch = new Stopwatch();
            stopWatch.Start();

            if (_scrapeConfig != null && _scrapeConfig.EnableParallelProcessing)
                readingListsFinal = DoPopulateReadingListsParallel(readingLists);
            else
                readingListsFinal = DoPopulateReadingLists(readingLists);


            stopWatch.Start();

            _scrapeReport.ScrapeEnded = DateTime.Now;
            _scrapeReport.TimeTaken = stopWatch.Elapsed;

            if (ScrapeEnded != null) ScrapeEnded(this, new ScrapeEndedEventArgs(ScrapeType.Books));

            return readingListsFinal;
        }


        //TODO: Flesh this out!
        private Book ParseBookInfoFromJson(string uri, string json)
        {
            var book = new Book();

            var jObj = JObject.Parse(json);


            var rawOrg = jObj.Properties().FirstOrDefault(p => p.Name.Contains("/organisations/"));
            var rawResources = jObj.Properties().Where(p => p.Name.Contains("/resources/") && !p.Name.Contains("/authors"));



            try
            {
                if (rawOrg.HasContent())
                {
                    foreach (var child in rawOrg.Children())
                    {
                        if (child.HasValues && child["http://xmlns.com/foaf/0.1/name"] != null)
                        {
                            var childa = JsonConvert.DeserializeObject<Element>(child["http://xmlns.com/foaf/0.1/name"][0].ToString());

                            book.Publisher = childa != null ? childa.Value : string.Empty;
                        }
                    }
                }

                if (rawResources.HasContent())
                {
                    foreach (var resource in rawResources)
                    {
                        var resJson = resource.ToString();

                        var replaceRootRegex = new Regex(RootRegex);

                        var finalJson = replaceRootRegex.Replace(resJson, "\"root\"", 1);

                        var resourceObj = JsonConvert.DeserializeObject<Resources>("{" + finalJson + "}");

                        if (resourceObj != null && resourceObj.Items != null)
                        {
                            if (resourceObj.Items.Title.HasContent())
                                book.Title += string.Join(", ", resourceObj.Items.Title.Select(n => n.Value));

                            book.URL = uri;
                        }
                    }

                }



            }
            catch (Exception ex)
            {
                Log.Error(ex);

                book = null;
            }


            return book;
        }

        #endregion

        public bool CancelScrape()
        {
            _scrapeCancelled = true;

            if(ScrapeCancelled != null) ScrapeCancelled(this, new ScrapeCancelledEventArgs());

            return true;
        }

        public ScrapeReport FetchScrapeReport()
        {
            return _scrapeReport;
        }
    }
}
