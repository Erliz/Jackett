using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jackett.Models;
using Newtonsoft.Json.Linq;
using NLog;
using Jackett.Utils;
using CsQuery;
using System.Web;
using Jackett.Services;
using Jackett.Utils.Clients;
using System.Text.RegularExpressions;
using Jackett.Models.IndexerConfig;
using System.Globalization;
using Newtonsoft.Json;
using Jackett.Models.IndexerConfig.Bespoke;

namespace Jackett.Indexers
{
    public class NewStudio : BaseIndexer, IIndexer
    {
        private string SearchUrl { get { return configData.Url.Value + "tracker.php"; } }
        private string LoginUrl { get { return configData.Url.Value + "login.php"; } }
        readonly static string defaultSiteLink = "http://newstudio.tv/";

        new ConfigurationDataNewStudio configData
        {
            get { return (ConfigurationDataNewStudio)base.configData; }
            set { base.configData = value; }
        }

        public NewStudio(IIndexerManagerService i, Logger l, IWebClient wc, IProtectionService ps)
            : base(name: "NewStudio",
                description: "Торрент трекер NewStudio",
                link: "http://newstudio.tv/",
                caps: TorznabUtil.CreateDefaultTorznabTVCaps(),
                manager: i,
                client: wc,
                logger: l,
                p: ps,
                configData: new ConfigurationDataNewStudio(defaultSiteLink))
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
                    { "login_username", configData.Username.Value },
                    { "login_password", configData.Password.Value },
                    { "autologin", "1" },
                    { "login", "1" }
            };
            
            var response = await RequestLoginAndFollowRedirect(LoginUrl, pairs, string.Empty, true, SiteLink);
            await ConfigureIfOK(response.Cookies, response.Content != null && response.Content.Contains("/login.php?logout=1"), () =>
            {
                configData = oldConfig;
                CQ dom = response.Content;
                var errorMessage = dom[".alert.alert-error"].Text().Trim().Replace("\n\t", " ");
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
            configData = JsonConvert.DeserializeObject<ConfigurationDataNewStudio>(json);
            IsConfigured = true;
        }

        public async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();
            var searchString = query.GetQueryString();
            
            List<KeyValuePair<string, string>> queryData = new List<KeyValuePair<string, string>>()
            {
                new KeyValuePair<string, string>("max", "1"),
                new KeyValuePair<string, string>("to", "1")
            };
            
            if (!string.IsNullOrWhiteSpace(searchString))
            {
                queryData.Add(new KeyValuePair<string, string>("nm", HttpUtility.UrlEncode(searchString.Trim())));
            }

            var results = await PostDataWithCookiesAndRetry(SearchUrl, queryData, string.Empty);
            try
            {
                CQ dom = results.Content;
                var rows = dom[".hl-tr"];
                foreach (var row in rows)
                {
                    var release = new ReleaseInfo();

                    release.MinimumRatio = 1;
                    release.MinimumSeedTime = 172800;
                    
                    var date = StringUtil.StripNonAlphaNumeric(Regex.Match(row.Cq().Find("td:eq(5)").Text(), @"(\d{1,2}\-\D{3}\-\d{2})").Groups[1].ToString().Trim()
                        .Replace("Янв", "01")
                        .Replace("Фев", "02")
                        .Replace("Мар", "03")
                        .Replace("Апр", "04")
                        .Replace("Май", "05")
                        .Replace("Июн", "06")
                        .Replace("Июл", "07")
                        .Replace("Авг", "08")
                        .Replace("Сен", "09")
                        .Replace("Окт", "10")
                        .Replace("Ноя", "11")
                        .Replace("Дек", "12"));
                    
                    if (date.Length < 8)
                    {
                        date = "0" + date;
                    }
                    release.PublishDate = DateTime.ParseExact(date, "dd-MM-yy", CultureInfo.InvariantCulture);

                    release.Category = TorznabCatType.TVWEBDL.ID;
                    var hasTorrent = row.Cq().Find("td:eq(4) a").Length > 0;
                    var title = row.Cq().Find("td:eq(3) b").Text().Trim();
                    // normallize title
                    release.Title = title
                        .Replace("WEBDLRip", "WEBDL 480p")
                        .Replace(" *Proper", "");

                    if (release.Title.IndexOf('|') > -1)
                    {
                        release.Title = release.Title.Substring(0, release.Title.IndexOf('|') - 1).Trim();
                    }

                    var releaseInfoMatch = Regex.Match(title, @"Сезон (\d+), Серия (\d+)");
                    var episode = releaseInfoMatch.Groups[2].ToString().Trim();
                    var season = releaseInfoMatch.Groups[1].ToString().Trim();
                    var seriesInfo = string.Format("s{0}e{1}", season.Length < 2 ? "0" + season : season, episode.Length < 2 ? "0" + episode : episode);

                    if (configData.StripRussian.Value)
                    {
                        var split = release.Title.IndexOf('/');
                        if (split > -1)
                        {
                            release.Title = release.Title.Substring(split + 1).Trim();
                        }
                    }
                    var titleChunks = release.Title.Split("()".ToCharArray());

                    release.Title = titleChunks[0] + seriesInfo + titleChunks[2] + " [Russian]";

                    release.Description = release.Title;
                    
                    var sizeStr = row.Cq().Find("td:eq(4) a:eq(0)").Text().Trim();
                    string[] sizeSplit = sizeStr.Split('\u00A0');
                    release.Size = ReleaseInfo.GetBytes(sizeSplit[1].ToLower(), ParseUtil.CoerceFloat(sizeSplit[0]));

                    release.Seeders = 10;
                    release.Peers = 10;

                    release.Guid = new Uri(configData.Url.Value + row.Cq().Find("td:eq(3) a:eq(0)").Attr("href").Substring(2));
                    release.Comments = release.Guid;

                    if (hasTorrent)
                    {
                        var torretUri = row.Cq().Find("td:eq(4) a:eq(0)").Attr("href").Substring(1);
                        // var magnetUri = row.Cq().Find("td:eq(1) a:eq(1)").Attr("href");
                        release.Link = new Uri(configData.Url.Value + torretUri);
                        // release.MagnetUri = new Uri(magnetUri);
                    }
                    else
                    {
                        // release.MagnetUri = new Uri(row.Cq().Find("td:eq(1) a:eq(0)").Attr("href"));
                    }

                    releases.Add(release);
                }
            }
            catch (Exception ex)
            {
                OnParseError(results.Content, ex);
            }

            return releases;
        }
    }
}
