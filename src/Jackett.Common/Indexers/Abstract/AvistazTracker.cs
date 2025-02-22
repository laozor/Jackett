using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig.Bespoke;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Jackett.Common.Utils.Clients;
using Newtonsoft.Json.Linq;
using NLog;
using WebClient = Jackett.Common.Utils.Clients.WebClient;

namespace Jackett.Common.Indexers.Abstract
{
    [ExcludeFromCodeCoverage]
    public abstract class AvistazTracker : IndexerBase
    {
        public override string Language => "en-US";
        public override string Type => "private";

        public override bool SupportsPagination => true;

        private readonly Dictionary<string, string> AuthHeaders = new Dictionary<string, string>
        {
            {"Accept", "application/json"},
            {"Content-Type", "application/json"}
        };
        private string AuthUrl => SiteLink + "api/v1/jackett/auth";
        private string SearchUrl => SiteLink + "api/v1/jackett/torrents";
        private readonly HashSet<string> _hdResolutions = new HashSet<string> { "1080p", "1080i", "720p" };
        private string _token;

        private new ConfigurationDataAvistazTracker configData => (ConfigurationDataAvistazTracker)base.configData;

        // hook to adjust the search term
        protected virtual string GetSearchTerm(TorznabQuery query) => $"{query.SearchTerm} {GetEpisodeSearchTerm(query)}".Trim();

        protected virtual string GetEpisodeSearchTerm(TorznabQuery query) => query.GetEpisodeSearchString().Trim();

        // hook to adjust the search category
        protected virtual List<KeyValuePair<string, string>> GetSearchQueryParameters(TorznabQuery query)
        {
            var categoryMapping = MapTorznabCapsToTrackers(query).Distinct().ToList();

            var qc = new List<KeyValuePair<string, string>> // NameValueCollection don't support cat[]=19&cat[]=6
            {
                { "in", "1" },
                { "type", categoryMapping.FirstIfSingleOrDefault("0") },
                { "limit", "50" }
            };

            if (query.Limit > 0 && query.Offset > 0)
            {
                var page = query.Offset / query.Limit + 1;
                qc.Add("page", page.ToString());
            }

            // resolution filter to improve the search
            if (!query.Categories.Contains(TorznabCatType.Movies.ID) && !query.Categories.Contains(TorznabCatType.TV.ID) &&
                !query.Categories.Contains(TorznabCatType.Audio.ID))
            {
                if (query.Categories.Contains(TorznabCatType.MoviesUHD.ID) || query.Categories.Contains(TorznabCatType.TVUHD.ID))
                    qc.Add("video_quality[]", "6"); // 2160p
                if (query.Categories.Contains(TorznabCatType.MoviesHD.ID) || query.Categories.Contains(TorznabCatType.TVHD.ID))
                {
                    qc.Add("video_quality[]", "2"); // 720p
                    qc.Add("video_quality[]", "7"); // 1080i
                    qc.Add("video_quality[]", "3"); // 1080p
                }
                if (query.Categories.Contains(TorznabCatType.MoviesSD.ID) || query.Categories.Contains(TorznabCatType.TVSD.ID))
                    qc.Add("video_quality[]", "1"); // SD
            }

            // note, search by tmdb and tvdb are supported too
            // https://privatehd.to/api/v1/jackett/torrents?tmdb=1234
            // https://privatehd.to/api/v1/jackett/torrents?tvdb=3653
            if (query.IsImdbQuery)
            {
                qc.Add("imdb", query.ImdbID);
                qc.Add("search", GetEpisodeSearchTerm(query));
            }
            else if (query.IsTmdbQuery)
            {
                qc.Add("tmdb", query.TmdbID.ToString());
                qc.Add("search", GetEpisodeSearchTerm(query));
            }
            else if (query.IsTvdbSearch)
            {
                qc.Add("tvdb", query.TvdbID.ToString());
                qc.Add("search", GetEpisodeSearchTerm(query));
            }
            else
                qc.Add("search", GetSearchTerm(query).Trim());

            if (!string.IsNullOrWhiteSpace(query.Genre))
                qc.Add("tags", query.Genre);

            if (configData.Freeleech.Value)
                qc.Add("discount[]", "1");

            return qc;
        }

        // hook to adjust category parsing
        protected virtual List<int> ParseCategories(TorznabQuery query, JToken row)
        {
            var cats = new List<int>();
            var resolution = row.Value<string>("video_quality");
            switch (row.Value<string>("type"))
            {
                case "Movie":
                    cats.Add(resolution switch
                    {
                        var res when _hdResolutions.Contains(res) => TorznabCatType.MoviesHD.ID,
                        "2160p" => TorznabCatType.MoviesUHD.ID,
                        _ => TorznabCatType.MoviesSD.ID
                    });
                    break;
                case "TV-Show":
                    cats.Add(resolution switch
                    {
                        var res when _hdResolutions.Contains(res) => TorznabCatType.TVHD.ID,
                        "2160p" => TorznabCatType.TVUHD.ID,
                        _ => TorznabCatType.TVSD.ID
                    });
                    break;
                case "Music":
                    cats.Add(TorznabCatType.Audio.ID);
                    break;
                default:
                    throw new Exception("Error parsing category!");
            }
            return cats;
        }

        protected AvistazTracker(IIndexerConfigurationService configService, WebClient client, Logger logger, IProtectionService p, ICacheService cs)
            : base(configService: configService,
                   client: client,
                   logger: logger,
                   p: p,
                   cacheService: cs,
                   configData: new ConfigurationDataAvistazTracker())
        {
            webclient.requestDelay = 3;
        }

        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            LoadValuesFromJson(configJson);

            await RenewalTokenAsync();

            var releases = await PerformQuery(new TorznabQuery());
            await ConfigureIfOK(string.Empty, releases.Any(),
                                () => throw new Exception("Could not find releases."));

            return IndexerConfigurationStatus.Completed;
        }

        private async Task RenewalTokenAsync()
        {
            var body = new Dictionary<string, string>
            {
                { "username", configData.Username.Value.Trim() },
                { "password", configData.Password.Value.Trim() },
                { "pid", configData.Pid.Value.Trim() }
            };
            var result = await RequestWithCookiesAsync(AuthUrl, method: RequestType.POST, data: body, headers: AuthHeaders);
            var json = JObject.Parse(result.ContentString);
            _token = json.Value<string>("token");
            if (_token == null)
                throw new Exception(json.Value<string>("message"));
        }

        protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();

            var qc = GetSearchQueryParameters(query);
            var episodeSearchUrl = SearchUrl + "?" + qc.GetQueryString();

            var response = await RequestWithCookiesAndRetryAsync(episodeSearchUrl, headers: GetSearchHeaders());
            if (response.Status == HttpStatusCode.Unauthorized || response.Status == HttpStatusCode.PreconditionFailed)
            {
                await RenewalTokenAsync();
                response = await RequestWithCookiesAndRetryAsync(episodeSearchUrl, headers: GetSearchHeaders());
            }
            else if (response.Status == HttpStatusCode.NotFound)
                return releases; // search without results, eg CinemaZ: tt0075998
            else if (response.Status != HttpStatusCode.OK)
                throw new Exception($"Unknown error: {response.ContentString}");

            try
            {
                var jsonContent = JToken.Parse(response.ContentString);
                foreach (var row in jsonContent.Value<JArray>("data"))
                {
                    var details = new Uri(row.Value<string>("url"));
                    var link = new Uri(row.Value<string>("download"));
                    var publishDate = DateTime.ParseExact(row.Value<string>("created_at"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

                    long? imdb = null;
                    long? tvdb = null;
                    long? tmdb = null;
                    var jMovieTv = row.Value<JToken>("movie_tv");
                    if (jMovieTv != null && jMovieTv.HasValues)
                    {
                        imdb = ParseUtil.GetImdbID(jMovieTv.Value<string>("imdb"));
                        if (long.TryParse(jMovieTv.Value<string>("tvdb"), out var tvdbParsed))
                            tvdb = tvdbParsed;
                        if (long.TryParse(jMovieTv.Value<string>("tmdb"), out var tmdbParsed))
                            tmdb = tmdbParsed;
                    }

                    var description = "";
                    var jAudio = row.Value<JArray>("audio");
                    if (jAudio != null && jAudio.HasValues)
                    {
                        var audioList = jAudio.Select(tag => tag.Value<string>("language")).ToList();
                        description += $"Audio: {string.Join(", ", audioList)}";
                    }
                    var jSubtitle = row.Value<JArray>("subtitle");
                    if (jSubtitle != null && jSubtitle.HasValues)
                    {
                        var subtitleList = jSubtitle.Select(tag => tag.Value<string>("language")).ToList();
                        description += $"<br/>Subtitles: {string.Join(", ", subtitleList)}";
                    }

                    var cats = ParseCategories(query, row);

                    var release = new ReleaseInfo
                    {
                        Title = row.Value<string>("file_name"),
                        Link = link,
                        InfoHash = row.Value<string>("info_hash"),
                        Details = details,
                        Guid = details,
                        Category = cats,
                        PublishDate = publishDate,
                        Description = description,
                        Size = row.Value<long>("file_size"),
                        Files = row.Value<long>("file_count"),
                        Grabs = row.Value<long>("completed"),
                        Seeders = row.Value<int>("seed"),
                        Peers = row.Value<int>("leech") + row.Value<int>("seed"),
                        Imdb = imdb,
                        TVDBId = tvdb,
                        TMDb = tmdb,
                        DownloadVolumeFactor = row.Value<double>("download_multiply"),
                        UploadVolumeFactor = row.Value<double>("upload_multiply"),
                        MinimumRatio = 1,
                        MinimumSeedTime = 172800 // 48 hours
                    };

                    releases.Add(release);
                }
            }
            catch (Exception ex)
            {
                OnParseError(response.ContentString, ex);
            }
            return releases;
        }

        private Dictionary<string, string> GetSearchHeaders() => new Dictionary<string, string>
        {
            {"Authorization", $"Bearer {_token}"}
        };
    }
}
