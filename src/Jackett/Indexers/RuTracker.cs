using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jackett.Models;
using Newtonsoft.Json.Linq;
using NLog;
using Jackett.Utils;
using CsQuery;
using Jackett.Services;
using Jackett.Utils.Clients;
using System.Text.RegularExpressions;
using Jackett.Models.IndexerConfig;
using System.Globalization;
using Newtonsoft.Json;
using Jackett.Models.IndexerConfig.Bespoke;
using System.Text;
using System.Linq;

namespace Jackett.Indexers
{
    public class RuTracker : BaseIndexer, IIndexer
    {
        private string SearchUrl { get { return SiteLink + "forum/tracker.php"; } }
        private string LoginUrl { get { return SiteLink + "forum/login.php"; } }
        private string TopicUrl { get { return SiteLink + "forum/viewtopic.php"; } }
        private string NewUrl { get { return SiteLink + "forum/search.php?lst=1"; } }
        private string ReleaseUrl { get { return SiteLink + "forum/viewtopic.php?t="; } }
        readonly static private string SiteScheme = "https:";
        readonly static private int newTopicsToParse = 10;
        readonly static private int[] defaultCatalogsToSearch = new int[] { 124, 189, 2100, 2198, 22, 2366, 33, 4, 511, 7, 9, 911, 921, 93 };
        readonly static private int[] movieCatalogsToSearch = new int[] { 100, 101, 1213, 1235, 124, 1543, 1576, 1577, 1666, 1670, 187, 1900, 208, 209, 2090, 2091, 2092, 2093, 2097, 2109, 212, 2198, 2199, 22, 2200, 2201, 2220, 2221, 2258, 2339, 2343, 2365, 2459, 312, 313, 352, 376, 4, 484, 505, 511, 514, 521, 539, 549, 572, 656, 7, 709, 822, 905, 93, 930, 934, 941 };
        readonly static string defaultSiteLink = "http://rutracker.net/";

        new ConfigurationDataRuTracker configData
        {
            get { return (ConfigurationDataRuTracker)base.configData; }
            set { base.configData = value; }
        }

        public RuTracker(IIndexerManagerService i, Logger l, IWebClient wc, IProtectionService ps)
            : base(name: "RuTracker",
                description: "Torrent tracker RuTracker",
                link: defaultSiteLink,
                caps: new TorznabCapabilities(),
                manager: i,
                client: wc,
                logger: l,
                p: ps,
                downloadBase: defaultSiteLink + "forum/dl.php?t=",
                configData: new ConfigurationDataRuTracker(defaultSiteLink))
        {
            TorznabCaps.Categories.Add(TorznabCatType.Movies);
            TorznabCaps.Categories.Add(TorznabCatType.MoviesHD);
            TorznabCaps.Categories.Add(TorznabCatType.MoviesSD);
            TorznabCaps.Categories.Add(TorznabCatType.MoviesWEBDL);
            TorznabCaps.Categories.Add(TorznabCatType.MoviesDVD);
            TorznabCaps.Categories.Add(TorznabCatType.MoviesBluRay);
            TorznabCaps.Categories.Add(TorznabCatType.MoviesOther);
        }

        public override async Task<ConfigurationData> GetConfigurationForSetup()
        {
            var loginPage = await RequestStringWithCookies(LoginUrl, string.Empty);
            CQ loginDom = loginPage.Content;
            log(loginPage.Content);
            var config = new ConfigurationDataRuTracker(defaultSiteLink);
            log(loginDom[".forumline img"].Length.ToString());
            if (loginDom[".forumline img"].Length > 0)
            {
                try
                {
                    var captchaUrl = SiteScheme + loginDom[".forumline img"].Attr("src").ToString().Trim();
                    var captchaSID = loginDom["input[name=\"cap_sid\"]"].Attr("value").ToString().Trim();
                    var captchaTextFieldName = loginDom["input[autocomplete=\"off\"]"].Attr("name").ToString().Trim();
                    var captchaImage = await RequestBytesWithCookies(captchaUrl);
                    config.CaptchaImage.Value = captchaImage.Content;
                    config.CaptchaCookie.Value = captchaImage.Cookies;
                    config.CaptchaTextFieldName.Value = captchaTextFieldName;
                    config.CaptchaSID.Value = captchaSID;
                }
                catch (Exception ex)
                {
                    OnParseError(loginPage.Content, ex);
                    throw ex;
                }
            }
            else
            {
                config.CaptchaImage.Value = new byte[] {};
                config.CaptchaCookie.Value = "";
                config.CaptchaTextFieldName.Value = "";
                config.CaptchaSID.Value = "";
            }

            return config;
        }

        public async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            configData.LoadValuesFromJson(configJson);
            var oldConfig = configData;

            // Build login form
            var pairs = new Dictionary<string, string> {
                    { "login_username", configData.Username.Value },
                    { "login_password", configData.Password.Value },
                    { "login", "Вход"}
            };
            if (!string.IsNullOrWhiteSpace(configData.CaptchaSID.Value))
            {
                pairs.Add("redirect", LoginUrl);
                pairs.Add(configData.CaptchaSID.Name, configData.CaptchaSID.Value);
                pairs.Add(configData.CaptchaTextFieldName.Value, configData.CaptchaText.Value);
            }
            var response = await RequestLoginAndFollowRedirect(LoginUrl, pairs, configData.CaptchaCookie.Value, true, null, LoginUrl, true);
            log(response.Content);
            await ConfigureIfOK(response.Cookies, response.Content != null && response.Content.Contains("logged-in-as-uname"), () =>
            {
                log(response.Content);
                configData = oldConfig;
                CQ dom = response.Content;
                var errorMessage = dom["h4.warnColor1"].Text().Trim();
                throw new ExceptionWithConfigData(errorMessage, (ConfigurationDataRuTracker)configData);
            });

            return IndexerConfigurationStatus.RequiresTesting;
        }


        protected override void SaveConfig()
        {
            indexerService.SaveConfig(this as IIndexer, JsonConvert.SerializeObject(configData));
        }

        // Override to load legacy config format
        public override void LoadFromSavedConfiguration(JToken jsonConfig)
        {
            var json = jsonConfig.ToString();
            configData = JsonConvert.DeserializeObject<ConfigurationDataRuTracker>(json);
            IsConfigured = true;
        }

        public async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();

            if (query.GetQueryString().Length == 0)
            {
                releases = await getNewReleases();
            }
            else
            {
                releases = await getReleasesByQuery(query.GetQueryString());
            }

            return releases;
        }

        private List<KeyValuePair<string, string>> getFormedQueryForNew(String queryString = "")
        {
          return new List<KeyValuePair<string, string>>()
          {
              new KeyValuePair<string, string>("prev_new", "0"),
              new KeyValuePair<string, string>("prev_oop", "0"),
              new KeyValuePair<string, string>("o", "1"),
              new KeyValuePair<string, string>("s", "2"),
              new KeyValuePair<string, string>("tm", "-1"),
              new KeyValuePair<string, string>("pn", ""),
              new KeyValuePair<string, string>("nm", queryString),
              new KeyValuePair<string, string>("f", String.Join(",", defaultCatalogsToSearch)),
          };
        }

        private List<KeyValuePair<string, string>> getFormedQueryForSearch(String queryString = "", int[] catalogs = null)
        {
          if (catalogs == null) {
              catalogs = defaultCatalogsToSearch;
          }
          return new List<KeyValuePair<string, string>>()
          {
              new KeyValuePair<string, string>("prev_new", "0"),
              new KeyValuePair<string, string>("prev_oop", "0"),
              new KeyValuePair<string, string>("o", "7"),
              new KeyValuePair<string, string>("s", "2"),
              new KeyValuePair<string, string>("pn", ""),
              new KeyValuePair<string, string>("nm", queryString),
              new KeyValuePair<string, string>("f", String.Join(",", catalogs)),
          };
        }

        private async Task<List<ReleaseInfo>> getNewReleases()
        {
            var releases = new List<ReleaseInfo>();

            var results = await PostDataWithCookiesAndRetry(SearchUrl, getFormedQueryForNew(), null, SiteLink);
            try
            {
                CQ dom = results.Content;
                if (dom["tr.tCenter"].Length == 0)
                {
                    return releases;
                }
                int parsedTopics = 0;
                foreach (var releaseRow in dom["tr.tCenter"])
                {
                    parsedTopics++;
                    if (parsedTopics > newTopicsToParse)
                    {
                        break;
                    }

                    CQ releaseDom = releaseRow.Cq();
                    string releaseUrl = ReleaseUrl + releaseDom.Find(".t-title .tLink").Attr("data-topic_id").ToString().Trim();
                    var releasePage = await RequestStringWithCookiesAndRetry(releaseUrl, null, SearchUrl);

                    try
                    {
                        var release = getReleaseInfoFromReleasePage(releasePage.Content);
                        releases.Add(release);
                    }
                    catch (Exception ex)
                    {
                        OnParseError(releaseDom.ToString(), ex);
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                OnParseError(results.Content, ex);
            }

            return releases;
        }

        private async Task<List<ReleaseInfo>> getReleasesByQuery(String query)
        {

            var releases = new List<ReleaseInfo>();

            var pairs = getFormedQueryForSearch(query, movieCatalogsToSearch);
            var results = await PostDataWithCookiesAndRetry(SearchUrl, pairs, null, SiteLink);
            try
          {
              CQ dom = results.Content;
              if (dom["tr.tCenter"].Length == 0)
              {
                  return releases;
              }
              int parsedTopics = 0;
              foreach (var releaseRow in dom["tr.tCenter"])
              {
                  parsedTopics++;
                  if (parsedTopics > newTopicsToParse)
                  {
                      break;
                  }

                  CQ releaseDom = releaseRow.Cq();
                  string releaseUrl = ReleaseUrl + releaseDom.Find(".t-title .tLink").Attr("data-topic_id").ToString().Trim();
                  var releasePage = await RequestStringWithCookiesAndRetry(releaseUrl, null, SearchUrl);

                  try
                  {
                      var release = getReleaseInfoFromReleasePage(releasePage.Content);
                      releases.Add(release);
                  }
                  catch (Exception ex)
                  {
                        OnParseError(releaseDom.ToString(), ex);
                        continue;
                  }
              }
          }
          catch (Exception ex)
          {
                OnParseError(results.Content, ex);
            }

            return releases;
        }

        private ReleaseInfo getReleaseInfoFromReleasePage(CQ dom)
        {
            var release = new ReleaseInfo();

            release.MinimumRatio = 1;
            release.MinimumSeedTime = 172800;
            release.PublishDate = getReleasePublishDate(dom);
            release.MagnetUri = getReleaseMagnetUri(dom);
            release.Link = getReleaseLink(dom);
            release.InfoHash = getReleaseTorrentHash(dom);
            release.Size = getReleaseSize(dom);
            release.Seeders = getReleaseSeeders(dom);
            release.Peers = getReleasePeer(dom);
            release.Comments = getReleaseComments(dom);
            release.Guid = release.Comments;

            release.Category = getMovieCatIdFromTitle(getTitleFromPage(dom));
            release.Title = getMovieReleaseTitle(dom);

            return release;
        }

        private Uri getReleaseComments(CQ dom)
        {
            return new Uri(ReleaseUrl + getReleaseId(dom));
        }

        private string getTitleFromPage(CQ dom)
        {
            return dom.Find("#topic-title").Text().Trim();
        }

        private string getTvReleaseTitle(CQ dom)
        {
            // Игра престолов / Game of Thrones / Сезон: 6 / Серии: 1-2 из 10 (Алан Тейлор, Даниэль Минахан) [2016, Фэнтези, драма, приключения, HDTV 720p] Dub (Кравец) + MVO (LostFilm | AlexFilm | FocusStudio | FOX | Omikron) + Original + Subs (Rus, Eng)
            string title = getTitleFromPage(dom);
            string name = getNameFromTitle(title);
            string quality = getQualityStringFromTitle(title);
            string dimension = getDimensionFromTitle(title);
            string language = getReleaseLanguage(dom);
            int seasonNum = getSeasonNumFromTitle(title);
            string episode = getEpisodeNumFromTitle(title);

            return "{name} S{season}E{episode} {quality} {dimension} [{lang}]"
                .Replace("{name}", name)
                .Replace("{season}", seasonNum.ToString())
                .Replace("{episode}", episode)
                .Replace("{quality}", quality)
                .Replace("{dimension}", dimension)
                .Replace("{lang}", language);
        }

        private string getMovieReleaseTitle(CQ dom)
        {
            // Дэдпул / Deadpool (Тим Миллер / Tim Miller) [2016, США, Канада, фантастика, боевик, приключения, комедия, Blu-ray disc 1080p] Dub + Sub Rus, Eng + Original Eng
            string title = getTitleFromPage(dom);
            string year = getYearFromTitle(title);
            string name = getNameFromTitle(title);
            string quality = getQualityStringFromTitle(title);
            string dimension = getDimensionFromTitle(title);
            string language = getReleaseLanguage(dom);

            return "{name} ({year}) [{quality} {dimension}] {{lang}}"
                .Replace("{year}", year)
                .Replace("{name}", name)
                .Replace("{quality}", quality)
                .Replace("{dimension}", dimension)
                .Replace("{lang}", language);
        }

        private int getSeasonNumFromTitle(String title)
        {
            return convertStringToInt(Regex.Match(title, @"Сезон: (\d+)").Groups[1].ToString());
        }

        private String getEpisodeNumFromTitle(String title)
        {
            return Regex.Match(title, @"Серии: (\d+\-\d+)").Groups[1].ToString().Trim();
        }

        private string getNameFromTitle(String title)
        {
            var name = Regex.Match(title, @"^(.*?)[\[\(]").Groups[1].ToString().Split('/').Last().Trim();
            log(string.Format("Find release name '{0}'", name));
            return name;
        }

        private string getDimensionFromTitle(String title)
        {
            return Regex.Match(title, @"\d+(?:p|i)").Value.ToString().Trim();
        }

        private string getYearFromTitle(String title)
        {
            string year = Regex.Match(title, @"\[(\d{4})[^pi]").Groups[1].ToString().Trim();
            log(string.Format("Find releas year '{0}'", year));
            return year;
        }

        private int getReleasePeer(CQ dom)
        {
            return getReleaseSeeders(dom) + convertStringToInt(dom.Find(".leech b").Text().ToString().Trim());
        }

        private int getReleaseSeeders(CQ dom)
        {
            return convertStringToInt(dom.Find(".seed b").Text().ToString().Trim());
        }

        private long getReleaseSize(CQ dom)
        {
            var size = dom.Find("#tor-size-humn").Text().ToString().Replace("&nbsp", " ");
            log(string.Format("Find release size '{0}'", size));
            return ReleaseInfo.GetBytes(size);
        }

        private int convertStringToInt(String input)
        {
            input = stripNotNumbers(input);
            return input.Length > 0 ? ParseUtil.CoerceInt(input) : 0;
        }

        private String stripNotNumbers(String input)
        {
            return Regex.Replace(input, @"[^0-9]", "");
        }

        private int getSeasonNumFromQuery(String query)
        {
            string seasonQuery = Regex.Match(query, @" S\d+(?:E|$)").Value.ToString().Trim();
            int seasonNum = 0;
            if (seasonQuery.Length > 0)
            {
                seasonNum = convertStringToInt(seasonQuery);
            }
            return seasonNum;
        }

        private int getEpisodeNumFromQuery(String query)
        {
            string episodeQuery = Regex.Match(query, @" S\d+(E\d+)$").Groups[1].ToString().Trim();
            int episodeNum = 0;
            if (episodeQuery.Length > 0)
            {
                episodeNum = convertStringToInt(episodeQuery);
            }
            return episodeNum;
        }

        private String getReleaseLanguage(CQ episodeDom)
        {
            return "Russian";
        }

        private string getReleaseTorrentHash(CQ dom)
        {
            return dom.Find("#tor-hash").ToString().Trim();
        }

        private Uri getReleaseMagnetUri(CQ dom)
        {
            CQ link = dom.Find(".magnet-link-16");
            if (link.Length == 0)
            {
                throw new Exception(string.Format("Fail to find magnet url in"));
            }
            string url = dom.Find(".magnet-link-16").Attr("href").Trim();
            log(string.Format("Find release magnet url {0}", url));
            return new Uri(url);
        }

        private string getReleaseId(CQ dom)
        {
            return Regex.Match(
                dom.Find(".dl-link").Attr("href").Trim(),
                @"\d+"
            ).Value.Trim();
        }

        private Uri getReleaseLink(CQ dom)
        {
            string url = downloadUrlBase + getReleaseId(dom);
            log(string.Format("Find release torrent link {0}", url));
            return new Uri(url);
        }

        private DateTime getReleasePublishDate(CQ dom)
        {
            string date = Regex.Match(dom.Find("#tor-reged").Html(), @"\[ (\d{2}\-\w{3}\-\d{2} \d{2}\:\d{2}) ]").Groups[1].ToString().Trim();
            if (date.Length > 0)
            {
                return DateTime.ParseExact(
                    date,
                    "yyyy-MMM-dd hh:mm",
                    CultureInfo.InvariantCulture
                );
            }

            return new DateTime();
        }

        private int getMovieCatIdFromTitle(String title)
        {
            int catId = TorznabCatType.Movies.ID;
            switch (getQualityStringFromTitle(title)) {
                case "BR-Disc":
                case "BR-Rip":
                    catId = TorznabCatType.MoviesBluRay.ID;
                    break;
                case "DVD":
                    catId = TorznabCatType.MoviesDVD.ID;
                    break;
                case "WEBDL":
                    catId = TorznabCatType.MoviesWEBDL.ID;
                    break;
                case "HD":
                    catId = TorznabCatType.MoviesHD.ID;
                    break;
                case "SD":
                    catId = TorznabCatType.MoviesSD.ID;
                    break;
                case "Other":
                    catId = TorznabCatType.MoviesOther.ID;
                    break;
            }

            return catId;
        }

        // coachpotato badly parse BluRay type =(
        private string getQualityStringFromTitle(String title)
        {
            if (
                Regex.IsMatch(title, @"blu\-?ray", RegexOptions.IgnoreCase)
            )
            {
                return "BR-Disc";
            }
            else if (
                Regex.IsMatch(title, @"bd\-?rip", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(title, @"bd\-?remux", RegexOptions.IgnoreCase)
            )
            {
                return "BR-Rip";
            }
            else if (
                Regex.IsMatch(title, @"DVD")
            )
            {
                return "DVD";
            }
            else if (
                Regex.IsMatch(title, @"web\-?dl", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(title, @"web\-?rip", RegexOptions.IgnoreCase)
            )
            {
                return "WEBDL";
            }
            else if (
                Regex.IsMatch(title, @"HDTV") ||
                Regex.IsMatch(title, @"hd\-?rip", RegexOptions.IgnoreCase)
            )
            {
                return "HD";
            }
            else if (
                Regex.IsMatch(title, @"SDTV", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(title, @"sat\-?rip", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(title, @"TeleCine", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(title, @"TV\-?Rip", RegexOptions.IgnoreCase)
            )
            {
                return "SD";
            }
            else if (
                Regex.IsMatch(title, @"cam\-?rip", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(title, @"VHS\-?Rip", RegexOptions.IgnoreCase)
            )
            {
                return "Other";
            }

            logger.Warn(String.Format("Couldn`t detect quality string from '{0}'", title));
            return "";
        }
        
        private void log(String message)
        {
            logger.Debug(message);
        }
    }
}
