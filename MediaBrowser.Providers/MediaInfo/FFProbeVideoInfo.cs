#pragma warning disable CA1068, CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Providers.MediaInfo
{
    public class FFProbeVideoInfo
    {
        private readonly ILogger<FFProbeVideoInfo> _logger;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IBlurayExaminer _blurayExaminer;
        private readonly ILocalizationManager _localization;
        private readonly IChapterManager _chapterManager;
        private readonly IServerConfigurationManager _config;
        private readonly ISubtitleManager _subtitleManager;
        private readonly ILibraryManager _libraryManager;
        private readonly AudioResolver _audioResolver;
        private readonly SubtitleResolver _subtitleResolver;
        private readonly IMediaAttachmentRepository _mediaAttachmentRepository;
        private readonly IMediaStreamRepository _mediaStreamRepository;

        public FFProbeVideoInfo(
            ILogger<FFProbeVideoInfo> logger,
            IMediaSourceManager mediaSourceManager,
            IMediaEncoder mediaEncoder,
            IBlurayExaminer blurayExaminer,
            ILocalizationManager localization,
            IChapterManager chapterManager,
            IServerConfigurationManager config,
            ISubtitleManager subtitleManager,
            ILibraryManager libraryManager,
            AudioResolver audioResolver,
            SubtitleResolver subtitleResolver,
            IMediaAttachmentRepository mediaAttachmentRepository,
            IMediaStreamRepository mediaStreamRepository)
        {
            _logger = logger;
            _mediaSourceManager = mediaSourceManager;
            _mediaEncoder = mediaEncoder;
            _blurayExaminer = blurayExaminer;
            _localization = localization;
            _chapterManager = chapterManager;
            _config = config;
            _subtitleManager = subtitleManager;
            _libraryManager = libraryManager;
            _audioResolver = audioResolver;
            _subtitleResolver = subtitleResolver;
            _mediaAttachmentRepository = mediaAttachmentRepository;
            _mediaStreamRepository = mediaStreamRepository;
        }

        public async Task<ItemUpdateType> ProbeVideo<T>(
            T item,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken)
            where T : Video
        {
            BlurayDiscInfo? blurayDiscInfo = null;

            Model.MediaInfo.MediaInfo? mediaInfoResult = null;

            if (!item.IsShortcut || options.EnableRemoteContentProbe)
            {
                if (item.VideoType == VideoType.Dvd)
                {
                    // Get list of playable .vob files
                    var vobs = _mediaEncoder.GetPrimaryPlaylistVobFiles(item.Path, null);

                    // Return if no playable .vob files are found
                    if (vobs.Count == 0)
                    {
                        _logger.LogError("No playable .vob files found in DVD structure, skipping FFprobe.");
                        return ItemUpdateType.MetadataImport;
                    }

                    // Fetch metadata of first .vob file
                    mediaInfoResult = await GetMediaInfo(
                        new Video
                        {
                            Path = vobs[0]
                        },
                        cancellationToken).ConfigureAwait(false);

                    // Sum up the runtime of all .vob files skipping the first .vob
                    for (var i = 1; i < vobs.Count; i++)
                    {
                        var tmpMediaInfo = await GetMediaInfo(
                            new Video
                            {
                                Path = vobs[i]
                            },
                            cancellationToken).ConfigureAwait(false);

                        mediaInfoResult.RunTimeTicks += tmpMediaInfo.RunTimeTicks;
                    }
                }
                else if (item.VideoType == VideoType.BluRay)
                {
                    // Use bluray: protocol via ffprobe — libbluray handles playlist
                    // selection and stream demuxing natively
                    try
                    {
                        mediaInfoResult = await GetMediaInfo(item, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A broken/empty BDMV structure must not fail the whole metadata refresh
                        _logger.LogWarning(ex, "FFprobe failed on Blu-ray structure {Path}, skipping FFprobe.", item.Path);
                        return ItemUpdateType.MetadataImport;
                    }

                    // BDInfo is still needed for chapter marks, which libbluray does
                    // not expose through ffprobe, and for the playlist's full stream
                    // table, since ffprobe only reports streams seen within its
                    // probe window.
                    var (discRoot, _) = EncodingHelper.ParseBlurayPath(item.Path);
                    var playlistName = item.Path.EndsWith(".mpls", StringComparison.OrdinalIgnoreCase)
                        ? Path.GetFileName(item.Path)
                        : null;
                    blurayDiscInfo = GetBDInfo(discRoot, playlistName);
                }
                else
                {
                    mediaInfoResult = await GetMediaInfo(item, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            await Fetch(item, cancellationToken, mediaInfoResult, blurayDiscInfo, options).ConfigureAwait(false);

            return ItemUpdateType.MetadataImport;
        }

        private Task<Model.MediaInfo.MediaInfo> GetMediaInfo(
            Video item,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = item.Path;
            var protocol = item.PathProtocol ?? MediaProtocol.File;

            if (item.IsShortcut)
            {
                path = item.ShortcutPath;
                protocol = _mediaSourceManager.GetPathProtocol(path);
            }

            return _mediaEncoder.GetMediaInfo(
                new MediaInfoRequest
                {
                    ExtractChapters = true,
                    MediaType = DlnaProfileType.Video,
                    MediaSource = new MediaSourceInfo
                    {
                        Path = path,
                        Protocol = protocol,
                        VideoType = item.VideoType,
                        IsoType = item.IsoType
                    }
                },
                cancellationToken);
        }

        protected async Task Fetch(
            Video video,
            CancellationToken cancellationToken,
            Model.MediaInfo.MediaInfo? mediaInfo,
            BlurayDiscInfo? blurayInfo,
            MetadataRefreshOptions options)
        {
            List<MediaStream> mediaStreams = new List<MediaStream>();
            IReadOnlyList<MediaAttachment> mediaAttachments;
            ChapterInfo[] chapters;

            await AddExternalAudioAsync(video, mediaStreams, options, cancellationToken).ConfigureAwait(false);

            if (mediaInfo is not null)
            {
                mediaStreams.AddRange(mediaInfo.MediaStreams);

                mediaAttachments = mediaInfo.MediaAttachments;
                video.TotalBitrate = mediaInfo.Bitrate;
                video.RunTimeTicks = mediaInfo.RunTimeTicks;
                video.Container = mediaInfo.Container;
                var videoType = video.VideoType;
                if (videoType == VideoType.BluRay || videoType == VideoType.Dvd)
                {
                    video.Size = mediaInfo.Size;
                }

                chapters = mediaInfo.Chapters ?? [];
                if (blurayInfo is not null)
                {
                    FetchBdInfo(ref chapters, mediaStreams, blurayInfo);
                }
            }
            else
            {
                foreach (var mediaStream in video.GetMediaStreams())
                {
                    if (!mediaStream.IsExternal)
                    {
                        mediaStreams.Add(mediaStream);
                    }
                }

                mediaAttachments = [];
                chapters = [];
            }

            // Download and insert external streams before the streams from the file to preserve stream IDs on remote videos
            await AddExternalSubtitlesAsync(video, mediaStreams, options, cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < mediaStreams.Count; i++)
            {
                mediaStreams[i].Index = i;
            }

            var libraryOptions = _libraryManager.GetLibraryOptions(video);

            if (mediaInfo is not null)
            {
                FetchEmbeddedInfo(video, mediaInfo, options, libraryOptions);
                FetchPeople(video, mediaInfo, options);
                video.Timestamp = mediaInfo.Timestamp;
                video.Video3DFormat ??= mediaInfo.Video3DFormat;
            }

            if (libraryOptions.AllowEmbeddedSubtitles == EmbeddedSubtitleOptions.AllowText || libraryOptions.AllowEmbeddedSubtitles == EmbeddedSubtitleOptions.AllowNone)
            {
                _logger.LogDebug("Disabling embedded image subtitles for {Path} due to DisableEmbeddedImageSubtitles setting", video.Path);
                mediaStreams.RemoveAll(i => i.Type == MediaStreamType.Subtitle && !i.IsExternal && !i.IsTextSubtitleStream);
            }

            if (libraryOptions.AllowEmbeddedSubtitles == EmbeddedSubtitleOptions.AllowImage || libraryOptions.AllowEmbeddedSubtitles == EmbeddedSubtitleOptions.AllowNone)
            {
                _logger.LogDebug("Disabling embedded text subtitles for {Path} due to DisableEmbeddedTextSubtitles setting", video.Path);
                mediaStreams.RemoveAll(i => i.Type == MediaStreamType.Subtitle && !i.IsExternal && i.IsTextSubtitleStream);
            }

            var videoStream = mediaStreams.FirstOrDefault(i => i.Type == MediaStreamType.Video);

            video.Height = videoStream?.Height ?? 0;
            video.Width = videoStream?.Width ?? 0;

            video.DefaultVideoStreamIndex = videoStream?.Index;

            video.HasSubtitles = mediaStreams.Any(i => i.Type == MediaStreamType.Subtitle);

            _mediaStreamRepository.SaveMediaStreams(video.Id, mediaStreams, cancellationToken);

            _mediaAttachmentRepository.SaveMediaAttachments(video.Id, mediaAttachments, cancellationToken);

            if (options.MetadataRefreshMode == MetadataRefreshMode.FullRefresh
                || options.MetadataRefreshMode == MetadataRefreshMode.Default)
            {
                if (_config.Configuration.DummyChapterDuration > 0 && chapters.Length <= 1 && mediaStreams.Any(i => i.Type == MediaStreamType.Video))
                {
                    chapters = CreateDummyChapters(video);
                }

                NormalizeChapterNames(chapters);

                var extractDuringScan = false;
                if (libraryOptions is not null)
                {
                    extractDuringScan = libraryOptions.ExtractChapterImagesDuringLibraryScan;
                }

                await _chapterManager.RefreshChapterImages(video, options.DirectoryService, chapters, extractDuringScan, false, cancellationToken).ConfigureAwait(false);

                _chapterManager.SaveChapters(video, chapters);
            }
        }

        private void NormalizeChapterNames(ChapterInfo[] chapters)
        {
            for (int i = 0; i < chapters.Length; i++)
            {
                string? name = chapters[i].Name;
                // Check if the name is empty and/or if the name is a time
                // Some ripping programs do that.
                if (string.IsNullOrWhiteSpace(name)
                    || TimeSpan.TryParse(name, out _))
                {
                    chapters[i].Name = string.Format(
                        CultureInfo.InvariantCulture,
                        _localization.GetLocalizedString("ChapterNameValue"),
                        (i + 1).ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>
        /// Merges BDInfo data into the ffprobe results. ffprobe (via the bluray:
        /// protocol) is authoritative for stream properties and runtime, but on an
        /// mpegts input it only reports streams that appear within its probe
        /// window, so streams that first appear late in the title (typically PGS
        /// subtitles) can be missing entirely. BDInfo reads the playlist's stream
        /// table directly, so streams it reports beyond ffprobe's per-type counts
        /// are appended. Chapters always come from BDInfo, since libbluray does
        /// not expose them through ffprobe.
        /// </summary>
        private void FetchBdInfo(ref ChapterInfo[] chapters, List<MediaStream> mediaStreams, BlurayDiscInfo blurayInfo)
        {
            if (blurayInfo.Chapters is not null && blurayInfo.Chapters.Length > 0)
            {
                double[] brChapter = blurayInfo.Chapters;
                chapters = new ChapterInfo[brChapter.Length];
                for (int i = 0; i < brChapter.Length; i++)
                {
                    chapters[i] = new ChapterInfo
                    {
                        StartPositionTicks = TimeSpan.FromSeconds(brChapter[i]).Ticks
                    };
                }
            }

            AppendMissingBdStreams(mediaStreams, blurayInfo, MediaStreamType.Audio);
            AppendMissingBdStreams(mediaStreams, blurayInfo, MediaStreamType.Subtitle);
        }

        /// <summary>
        /// Appends streams of the given type that BDInfo reports for the playlist
        /// but ffprobe did not see. Never reorders or renumbers the ffprobe-derived
        /// streams: their Index values are what -map later uses.
        /// </summary>
        private void AppendMissingBdStreams(List<MediaStream> mediaStreams, BlurayDiscInfo blurayInfo, MediaStreamType type)
        {
            var probedStreams = mediaStreams.Where(s => !s.IsExternal && s.Type == type).ToList();
            var bdStreams = blurayInfo.MediaStreams.Where(s => s.Type == type).ToList();

            var missingCount = bdStreams.Count - probedStreams.Count;
            if (missingCount <= 0)
            {
                return;
            }

            // Greedy one-to-one matching on language + codec to find which BDInfo
            // streams ffprobe already reported.
            var unmatched = new List<MediaStream>();
            foreach (var bdStream in bdStreams)
            {
                var match = probedStreams.FirstOrDefault(s => BdLanguagesMatch(bdStream.Language, s.Language) && BdCodecsMatch(bdStream.Codec, s.Codec));
                if (match is not null)
                {
                    probedStreams.Remove(match);
                }
                else
                {
                    unmatched.Add(bdStream);
                }
            }

            // Only append when the unmatched set exactly accounts for the count
            // difference; anything else means the metadata is ambiguous and
            // appending could duplicate a stream ffprobe already reported.
            if (unmatched.Count != missingCount)
            {
                _logger.LogDebug(
                    "BDInfo reports {BdCount} {Type} streams for playlist {Playlist} but ffprobe saw {ProbedCount}; stream metadata is ambiguous, not appending",
                    bdStreams.Count,
                    type,
                    blurayInfo.PlaylistName,
                    bdStreams.Count - missingCount);
                return;
            }

            var nextIndex = mediaStreams.Count == 0 ? 0 : mediaStreams.Max(s => s.Index) + 1;
            foreach (var stream in unmatched)
            {
                _logger.LogInformation(
                    "Adding {Type} stream ({Codec}, {Language}) reported by BDInfo for playlist {Playlist} but not seen by ffprobe",
                    type,
                    stream.Codec,
                    stream.Language,
                    blurayInfo.PlaylistName);

                // These indexes continue past what ffprobe reported, so ffmpeg may
                // not be able to -map them until it has read far enough into the
                // title for the stream to appear.
                stream.Index = nextIndex++;
                mediaStreams.Add(stream);
            }
        }

        /// <summary>
        /// Compares a BDInfo language code against an ffprobe one. Missing tags on
        /// either side count as a match, so a stream ffprobe reported without a
        /// language tag is not appended a second time.
        /// </summary>
        private static bool BdLanguagesMatch(string? bdLanguage, string? probedLanguage)
        {
            if (string.IsNullOrEmpty(bdLanguage)
                || string.IsNullOrEmpty(probedLanguage)
                || string.Equals(bdLanguage, "und", StringComparison.OrdinalIgnoreCase)
                || string.Equals(probedLanguage, "und", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(bdLanguage, probedLanguage, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Compares a BDInfo codec name against an ffprobe one. BDInfo and ffprobe
        /// use different names for some codecs; unknown/missing codecs count as a
        /// match so ambiguous streams are never appended twice.
        /// </summary>
        private static bool BdCodecsMatch(string? bdCodec, string? probedCodec)
        {
            if (string.IsNullOrEmpty(bdCodec) || string.IsNullOrEmpty(probedCodec))
            {
                return true;
            }

            static string Normalize(string codec) => codec.ToLowerInvariant() switch
            {
                "avc" => "h264",
                "lpcm" => "pcm_bluray",
                "hdmv_pgs_subtitle" => "pgssub",
                var c => c
            };

            return string.Equals(Normalize(bdCodec), Normalize(probedCodec), StringComparison.Ordinal);
        }

        /// <summary>
        /// Gets Blu-ray disc info, optionally for a specific playlist.
        /// </summary>
        /// <param name="path">The disc root path.</param>
        /// <param name="playlistName">Optional playlist filename (e.g. "00202.mpls"). If null, uses the longest playlist.</param>
        /// <returns>BlurayDiscInfo.</returns>
        private BlurayDiscInfo? GetBDInfo(string path, string? playlistName = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);

            try
            {
                return playlistName is not null
                    ? _blurayExaminer.GetDiscInfo(path, playlistName)
                    : _blurayExaminer.GetDiscInfo(path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting BDInfo");
                return null;
            }
        }

        internal void FetchEmbeddedInfo(Video video, Model.MediaInfo.MediaInfo data, MetadataRefreshOptions refreshOptions, LibraryOptions libraryOptions)
        {
            var replaceData = refreshOptions.ReplaceAllMetadata;

            if (!video.IsLocked && !video.LockedFields.Contains(MetadataField.OfficialRating))
            {
                if (string.IsNullOrWhiteSpace(video.OfficialRating) || replaceData)
                {
                    video.OfficialRating = data.OfficialRating;
                }
            }

            if (!video.IsLocked && !video.LockedFields.Contains(MetadataField.Genres))
            {
                if (video.Genres.Length == 0 || replaceData)
                {
                    video.Genres = [];

                    foreach (var genre in data.Genres.Trimmed())
                    {
                        video.AddGenre(genre);
                    }
                }
            }

            if (!video.IsLocked && !video.LockedFields.Contains(MetadataField.Studios))
            {
                if (video.Studios.Length == 0 || replaceData)
                {
                    video.SetStudios(data.Studios);
                }
            }

            if (!video.IsLocked && video is MusicVideo musicVideo)
            {
                if (string.IsNullOrEmpty(musicVideo.Album) || replaceData)
                {
                    musicVideo.Album = data.Album;
                }

                if (musicVideo.Artists.Count == 0 || replaceData)
                {
                    musicVideo.Artists = data.Artists;
                }
            }

            // Extras have no release date of their own, they inherit it from the item they belong to.
            var useContainerDates = video.ExtraType is null;
            if (useContainerDates && data.ProductionYear is not null)
            {
                if (video.ProductionYear is null || replaceData)
                {
                    video.ProductionYear = data.ProductionYear;
                }
            }

            if (useContainerDates && data.PremiereDate is not null)
            {
                if (video.PremiereDate is null || replaceData)
                {
                    video.PremiereDate = data.PremiereDate;
                }
            }

            if (data.IndexNumber.HasValue)
            {
                if (!video.IndexNumber.HasValue || replaceData)
                {
                    video.IndexNumber = data.IndexNumber;
                }
            }

            if (data.ParentIndexNumber.HasValue)
            {
                if (!video.ParentIndexNumber.HasValue || replaceData)
                {
                    video.ParentIndexNumber = data.ParentIndexNumber;
                }
            }

            if (!video.IsLocked && !video.LockedFields.Contains(MetadataField.Name))
            {
                if (!string.IsNullOrWhiteSpace(data.Name) && libraryOptions.EnableEmbeddedTitles)
                {
                    // Separate option to use the embedded name for extras because it will often be the same name as the movie
                    if (!video.ExtraType.HasValue || libraryOptions.EnableEmbeddedExtrasTitles)
                    {
                        video.Name = data.Name;
                    }
                }

                if (!string.IsNullOrWhiteSpace(data.ForcedSortName))
                {
                    video.ForcedSortName = data.ForcedSortName;
                }
            }

            // If we don't have a ProductionYear try and get it from PremiereDate
            if (useContainerDates && video.PremiereDate is not null && video.ProductionYear is null)
            {
                video.ProductionYear = video.PremiereDate.Value.ToLocalTime().Year;
            }

            if (!video.IsLocked && !video.LockedFields.Contains(MetadataField.Overview))
            {
                if (string.IsNullOrWhiteSpace(video.Overview) || replaceData)
                {
                    video.Overview = data.Overview;
                }
            }
        }

        private void FetchPeople(Video video, Model.MediaInfo.MediaInfo data, MetadataRefreshOptions options)
        {
            if (video.IsLocked
                || video.LockedFields.Contains(MetadataField.Cast)
                || data.People.Length == 0)
            {
                return;
            }

            if (options.ReplaceAllMetadata || _libraryManager.GetPeople(video).Count == 0)
            {
                var people = new List<PersonInfo>();

                foreach (var person in data.People)
                {
                    if (!string.IsNullOrWhiteSpace(person.Name))
                    {
                        PeopleHelper.AddPerson(people, new PersonInfo
                        {
                            Name = person.Name,
                            Type = person.Type,
                            Role = person.Role?.Trim()
                        });
                    }
                }

                _libraryManager.UpdatePeople(video, people);
            }
        }

        /// <summary>
        /// Adds the external subtitles.
        /// </summary>
        /// <param name="video">The video.</param>
        /// <param name="currentStreams">The current streams.</param>
        /// <param name="options">The refreshOptions.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        private async Task AddExternalSubtitlesAsync(
            Video video,
            List<MediaStream> currentStreams,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken)
        {
            var externalSubtitleStreams = await _subtitleResolver.GetExternalStreamsAsync(video, 0, options.DirectoryService, false, cancellationToken).ConfigureAwait(false);

            var enableSubtitleDownloading = options.MetadataRefreshMode == MetadataRefreshMode.Default ||
                                            options.MetadataRefreshMode == MetadataRefreshMode.FullRefresh;

            var libraryOptions = _libraryManager.GetLibraryOptions(video);

            if (enableSubtitleDownloading && libraryOptions.SubtitleDownloadLanguages is not null)
            {
                var downloadedLanguages = await new SubtitleDownloader(
                    _logger,
                    _subtitleManager).DownloadSubtitles(
                        video,
                        currentStreams.Concat(externalSubtitleStreams).ToList(),
                        libraryOptions.SkipSubtitlesIfEmbeddedSubtitlesPresent,
                        libraryOptions.SkipSubtitlesIfAudioTrackMatches,
                        libraryOptions.RequirePerfectSubtitleMatch,
                        libraryOptions.SubtitleDownloadLanguages,
                        libraryOptions.DisabledSubtitleFetchers,
                        libraryOptions.SubtitleFetcherOrder,
                        true,
                        cancellationToken).ConfigureAwait(false);

                // Rescan
                if (downloadedLanguages.Count > 0)
                {
                    externalSubtitleStreams = await _subtitleResolver.GetExternalStreamsAsync(video, 0, options.DirectoryService, true, cancellationToken).ConfigureAwait(false);
                }
            }

            video.SubtitleFiles = externalSubtitleStreams.Select(i => i.Path).Distinct().ToArray();

            currentStreams.InsertRange(0, externalSubtitleStreams);
        }

        /// <summary>
        /// Adds the external audio.
        /// </summary>
        /// <param name="video">The video.</param>
        /// <param name="currentStreams">The current streams.</param>
        /// <param name="options">The refreshOptions.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task AddExternalAudioAsync(
            Video video,
            List<MediaStream> currentStreams,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken)
        {
            var externalAudioStreams = await _audioResolver.GetExternalStreamsAsync(video, 0, options.DirectoryService, false, cancellationToken).ConfigureAwait(false);

            video.AudioFiles = externalAudioStreams.Select(i => i.Path).Distinct().ToArray();

            currentStreams.AddRange(externalAudioStreams);
        }

        /// <summary>
        /// Creates dummy chapters.
        /// </summary>
        /// <param name="video">The video.</param>
        /// <returns>An array of dummy chapters.</returns>
        internal ChapterInfo[] CreateDummyChapters(Video video)
        {
            var runtime = video.RunTimeTicks.GetValueOrDefault();

            // Only process files with a runtime greater than 0 and less than 12h. The latter are likely corrupted.
            if (runtime < 0 || runtime > TimeSpan.FromHours(12).Ticks)
            {
                throw new ArgumentException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} has an invalid runtime of {1} minutes",
                        video.Name,
                        TimeSpan.FromTicks(runtime).TotalMinutes));
            }

            long dummyChapterDuration = TimeSpan.FromSeconds(_config.Configuration.DummyChapterDuration).Ticks;

            if (runtime <= 0)
            {
                return [];
            }

            int chapterCount = Math.Max(1, (int)(runtime / dummyChapterDuration));
            var chapters = new ChapterInfo[chapterCount];

            long currentChapterTicks = 0;
            for (int i = 0; i < chapterCount; i++)
            {
                chapters[i] = new ChapterInfo
                {
                    StartPositionTicks = currentChapterTicks
                };

                currentChapterTicks += dummyChapterDuration;
            }

            return chapters;
        }
    }
}
