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

namespace Jackett.Indexers
{
    public class Soap4Me : BaseIndexer, IIndexer
    {
        private string SearchUrl { get { return SiteLink + "search/"; } }
        private string LoginUrl { get { return SiteLink + "login/"; } }
        private string CallbackUrl { get { return SiteLink + "callback/"; } }
        private string NewUrl { get { return SiteLink + "new/"; } }
        private string TorrentDownloadUrl { get { return SiteLink + "dl/{id}.torrent"; } }
        readonly static string defaultSiteLink = "https://soap4.me/";

        new ConfigurationDataSoap4Me configData
        {
            get { return (ConfigurationDataSoap4Me)base.configData; }
            set { base.configData = value; }
        }

        public Soap4Me(IIndexerManagerService i, Logger l, IWebClient wc, IProtectionService ps)
            : base(name: "Soap4Me",
                description: "Торрент трекер Soap4Me",
                link: defaultSiteLink,
                caps: TorznabUtil.CreateDefaultTorznabTVCaps(),
                manager: i,
                client: wc,
                logger: l,
                p: ps,
                downloadBase: defaultSiteLink + "dl/",
                configData: new ConfigurationDataSoap4Me(defaultSiteLink))
        {
            TorznabCaps.Categories.Add(TorznabCatType.TV);
            TorznabCaps.Categories.Add(TorznabCatType.TVWEBDL);
        }

        public async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            configData.LoadValuesFromJson(configJson);
            var oldConfig = configData;

            // Build login form
            var pairs = new Dictionary<string, string> {
                    { "login", configData.Username.Value },
                    { "password", configData.Password.Value },
            };

            var response = await RequestLoginAndFollowRedirect(LoginUrl, pairs, string.Empty, true, SiteLink);
            await ConfigureIfOK(response.Cookies, response.Content != null && response.Content.Contains("action=\"/logout\""), () =>
            {
                configData = oldConfig;
                CQ dom = response.Content;
                var errorMessage = dom["#message"].Text().Trim().Replace("\n\t", " ");
                throw new ExceptionWithConfigData(errorMessage, (ConfigurationData)configData);
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
            configData = JsonConvert.DeserializeObject<ConfigurationDataSoap4Me>(json);
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

        private async Task<List<ReleaseInfo>> getNewReleases()
        {
            var releases = new List<ReleaseInfo>();

            var results = await RequestStringWithCookiesAndRetry(NewUrl, null, SiteLink);
            try
            {
                CQ dom = results.Content;
                if (dom["#new .ep"].Length == 0)
                {
                    return releases;
                }

                foreach (var episodeRow in dom["#new .ep"])
                {
                    CQ episodeDom = episodeRow.Cq();

                    var release = getBaseReleaseInfo(episodeDom);
                    var mediaInfo = await getMediaInfo(episodeDom);

                    string serialTitle = episodeDom.Find(".soap").Text().ToString().Trim();
                    string episodeNums = episodeDom.Find(".nums").Text().ToString().Trim();
                    string translationGroup = getEpisodeTranslatorGroup(episodeDom);
                    if (translationGroup.Length > 0)
                    {
                        release.Title = "[" + translationGroup + "] ";
                    }
                    release.Title = release.Title + serialTitle + " " + episodeNums;

                    if (mediaInfo.Value<int>("ok") == 1)
                    {
                        release.Seeders = getEpisodeTorrentSeeders(mediaInfo);
                        release.Peers = getEpisodeTorrentPeers(mediaInfo);
                        release.Size = getEpisodeFileSize(mediaInfo);
                        release.Title = release.Title + " " + getEpisodeDimensions(mediaInfo);
                    }

                    release.Title = release.Title + " [" + getEpisodeLanguage(episodeDom) + "]";

                    releases.Add(release);
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
            var searchQuery = SearchUrl + "?q=" + query.Trim();

            var results = await RequestStringWithCookiesAndRetry(searchQuery, null, SiteLink);
            try
            {
                CQ dom = results.Content;
                if (dom["#search-soap a"].Length == 0)
                {
                    return releases;
                }
                var serialLink = SiteLink + dom["#search-soap a"].Attr("href").Trim();
                var serialPage = await RequestStringWithCookiesAndRetry(serialLink, null, searchQuery);
                CQ serialDom = serialPage.Content;

                var seasonsLinksTags = serialDom["#soap a"];
                foreach (var seasonLinkTag in seasonsLinksTags)
                {
                    string seasonLink = SiteLink + seasonLinkTag.Cq().Attr("href");
                    if (getSeasonNumFromQuery(query) > 0 && !Regex.IsMatch(seasonLink, @"/" + getSeasonNumFromQuery(query) + "/$"))
                    {
                        continue;
                    }
                    var seasonPage = await RequestStringWithCookiesAndRetry(seasonLink, null, serialLink);
                    CQ seasonDom = seasonPage.Content;
                    string seriesTitle = Regex.Match(seasonDom["#episodes h2 a"].Html(), @"(.*?) <").Groups[1].ToString().Trim();
                    string seasonNum = Regex.Match(seasonDom["#episodes h2 a"].Text(), @"\d+$").Value;
                    foreach (var episodeRow in seasonDom["#episodes .ep"])
                    {
                        CQ row = episodeRow.Cq();
                        string episodeNum = row.Find(".number").Text().Trim();
                        if ((getEpisodeNumFromQuery(query) > 0 && episodeNum == getEpisodeNumFromQuery(query).ToString()) || episodeNum == "--")
                        {
                            continue;
                        }
                        var release = getBaseReleaseInfo(row);
                        var mediaInfo = await getMediaInfo(row);
                        string translationGroup = getEpisodeTranslatorGroup(row);
                        if (translationGroup.Length > 0)
                        {
                            release.Title = "[" + translationGroup + "] ";
                        }
                        release.Title = release.Title + seriesTitle + " s" + seasonNum + "e" + episodeNum;

                        if (mediaInfo.Value<int>("ok") == 1)
                        {
                            release.Seeders = getEpisodeTorrentSeeders(mediaInfo);
                            release.Peers = getEpisodeTorrentPeers(mediaInfo);
                            release.Size = getEpisodeFileSize(mediaInfo);
                            release.Title = release.Title + " " + getEpisodeDimensions(mediaInfo);
                        }
                        release.Title = release.Title + " [" + getEpisodeLanguage(row) + "]";
                        releases.Add(release);
                    }
                }
            }
            catch (Exception ex)
            {
                OnParseError(results.Content, ex);
            }

            return releases;
        }

        private ReleaseInfo getBaseReleaseInfo(CQ episodedom)
        {
            var release = new ReleaseInfo();

            release.MinimumRatio = 1;
            release.MinimumSeedTime = 172800;
            release.Category = getEpisodeCategory(episodedom);
            release.Link = getEpisodeLink(episodedom);
            release.Description = getEpisodeDescription(episodedom);
            release.PublishDate = getEpisodePublishDate(episodedom);

            return release;
        }

        private long getEpisodeFileSize(JObject info)
        {
            string[] dimensions = getEpisodeDimensions(info).Split('x');
            int empiricalRate = 6;
            // minutes * 60 (seconds) x 24 (FPS) x 6 (BPC) x 3 (RGB or 4 x for RGBA) x 512 (horizontal dimension) x 512 (vertical dimension) = in bits
            return ReleaseInfo.GetBytes(
                "kb",
                (long) getEpisodeDurationSeconds(info) * 24 * getEpisodeBitRate(info) * 3 * ParseUtil.CoerceInt(dimensions[0]) * ParseUtil.CoerceInt(dimensions[1]) / empiricalRate / 8 / 1024f
            );
        }

        private int getEpisodeBitRate(JObject info)
        {
            int rate = convertStringToInt(info.Value<string>("bitrate")) / 1000;
            return rate > 0 ? rate : 1;
        }

        private int getEpisodeDurationSeconds(JObject info)
        {
            string duration = info.Value<string>("duration");
            int seconds = convertStringToInt(Regex.Match(duration, @"\d+s").Value.ToString().Trim());
            int minutes = convertStringToInt(Regex.Match(duration, @"\d+mn").Value.ToString().Trim());
            int hours = convertStringToInt(Regex.Match(duration, @"\d+h").Value.ToString().Trim());
            return hours * 60 * 60 + minutes * 60 +seconds;
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

        private String getEpisodeDimensions(JObject info)
        {
            return info.Value<String>("dimensions").Replace(" ", "");
        }

        private int getEpisodeTorrentSeeders(JObject info)
        {
            return info.Value<int>("seeds");
        }

        private int getEpisodeTorrentPeers(JObject info)
        {
            return getEpisodeTorrentSeeders(info) + info.Value<int>("peers");
        }

        private String getEpisodeDescription(CQ episodeDom)
        {
            return episodeDom.Find("p.text").Text().Trim();
        }

        private String getEpisodeTranslatorGroup(CQ episodeDom)
        {
            string translator = episodeDom.Find(".translate").Text().ToString().Trim();
            if (translator == "Субтитры")
            {
                translator = "";
            }

            return translator;
        }

        private String getEpisodeLanguage(CQ episodeDom)
        {
            return getEpisodeTranslatorGroup(episodeDom).Length > 0 ? "Russian" : "English";
        }

        private int getEpisodeCategory(CQ episodeDom)
        {
            return getCatIdByQualityString(episodeDom.Find(".quality").Text().Trim());
        }

        private Uri getEpisodeLink(CQ episodeDom)
        {
            return new Uri(SiteLink + episodeDom.Find("a.no-ajaxy").Attr("href"));
        }

        private DateTime getEpisodePublishDate(CQ episodeDom)
        {
            string date = Regex.Match(episodeDom.Find(".info div div").Html(), @"(\d{1,4}\-\d{2}\-\d{2})").Groups[1].ToString().Trim();
            if (date.Length > 0 && date != "0000-00-00")
            {
                return DateTime.ParseExact(
                    date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture
                );
            }

            return new DateTime();
        }

        private int getCatIdByQualityString(String qualityString)
        {
            int catId = TorznabCatType.TVSD.ID;
            switch (qualityString) {
                case "SD":
                    catId = TorznabCatType.TVSD.ID;
                    break;
                case "fullHD":
                case "HD":
                    catId = TorznabCatType.TVHD.ID;
                    break;
            }

            return catId;
        }

        private async Task<JObject> getMediaInfo(CQ episodeDom, String refererLink = null)
        {
            List<KeyValuePair<string, string>> queryData = new List<KeyValuePair<string, string>>()
            {
                new KeyValuePair<string, string>("eid", episodeDom.Find(".mediainfo.pointer").Attr("data:eid").ToString().Trim()),
                new KeyValuePair<string, string>("token", episodeDom["#token"].Attr("data:token").ToString().Trim()),
                new KeyValuePair<string, string>("what", "mediainfo")
            };

            Dictionary<String, String> headers = new Dictionary<String, String>()
            {
                {"X-Requested-With", "XMLHttpRequest"}
            };

            var mediaInfoJson = await PostDataWithCookiesAndRetry(CallbackUrl, queryData, null, refererLink, headers);
            var mediaInfo = JObject.Parse(mediaInfoJson.Content);

            return mediaInfo;
        }

    }
}
