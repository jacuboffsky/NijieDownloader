﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Nandaka.Common;
using NijieDownloader.Library;
using NijieDownloader.Library.DAL;
using NijieDownloader.Library.Model;
using NijieDownloader.UI.ViewModel;

namespace NijieDownloader.UI
{
    public class JobRunner
    {
        private static TaskFactory _jobFactory;
        private static LimitedConcurrencyLevelTaskScheduler jobScheduler;
        private static Object _lock = new Object();
        private static object _dbLock = new object();
        private static List<Task> tasks = new List<Task>();

        public static JobStatus BatchStatus { get; set; }

        /// <summary>
        /// Init the JobRunner, changes on these settings require app restart:
        /// - ConcurrentJob
        /// </summary>
        static JobRunner()
        {
            jobScheduler = new LimitedConcurrencyLevelTaskScheduler(Properties.Settings.Default.ConcurrentJob, 8);
            _jobFactory = new TaskFactory(jobScheduler);
        }

        #region main job method.

        /// <summary>
        /// Run job on Task factory
        /// </summary>
        /// <param name="job"></param>
        public static void DoJob(JobDownloadViewModel job, CancellationTokenSource cancelSource)
        {
            job.Status = JobStatus.Queued;
            job.CancelToken = cancelSource.Token;
            job.DownloadCount = 0;
            job.CurrentPage = 1;

            tasks.Add(_jobFactory.StartNew(() =>
            {
                long start = DateTime.Now.Ticks;
                double totalSecond = 0;
                try
                {
                    if (Properties.Settings.Default.JobDelay > 0)
                    {
                        MainWindow.Log.Debug(String.Format("Delay before starting job: {0}ms", Properties.Settings.Default.JobDelay));
                        Thread.Sleep(Properties.Settings.Default.JobDelay);
                    }

                    if (isJobCancelled(job))
                        return;

                    job.Status = JobStatus.Running;
                    job.Message = "Starting job...";
                    switch (job.JobType)
                    {
                        case JobType.Member:
                            doMemberJob(job);
                            break;

                        case JobType.Tags:
                            doSearchJob(job);
                            break;

                        case JobType.Image:
                            doImageJob(job);
                            break;
                    }

                    totalSecond = new TimeSpan(DateTime.Now.Ticks - start).TotalSeconds;
                }
                catch (Exception ex)
                {
                    job.Status = JobStatus.Error;
                    job.Message = Util.GetAllInnerExceptionMessage(ex);
                }
                finally
                {
                    if (job.Status != JobStatus.Error && job.Status != JobStatus.Cancelled)
                    {
                        job.Status = JobStatus.Completed;
                        job.Message = String.Format("Job completed in {0}s", totalSecond);
                    }
                }
                MainWindow.Log.Debug(String.Format("Job completed: {0} in {1}s", job.Name, totalSecond));
            }
            , cancelSource.Token
            , TaskCreationOptions.LongRunning | TaskCreationOptions.PreferFairness
            , jobScheduler
            ));
        }

        /// <summary>
        /// Notify all task completed callback
        /// </summary>
        /// <param name="action"></param>
        public static void NotifyAllCompleted(Action action)
        {
            var finalTask = _jobFactory.ContinueWhenAll(tasks.ToArray(), x =>
            {
                BatchStatus = JobStatus.Completed;
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
            });
        }

        /// <summary>
        /// Process individual image.
        /// </summary>
        /// <param name="job"></param>
        private static void doImageJob(JobDownloadViewModel job)
        {
            if (isJobCancelled(job))
                return;

            MainWindow.Log.Debug("Running Image Job: " + job.Name);
            try
            {
                NijieImage image = new NijieImage(job.ImageId);
                processImage(job, null, image);
            }
            catch (NijieException ne)
            {
                HandleJobException(job, ne);
                MainWindow.Log.Error("Error when processing Image Job: " + job.Name, ne);
            }
        }

        /// <summary>
        /// Process images from search result.
        /// </summary>
        /// <param name="job"></param>
        private static void doSearchJob(JobDownloadViewModel job)
        {
            if (isJobCancelled(job)) return;

            MainWindow.Log.Debug("Running Search Job: " + job.Name);
            try
            {
                job.CurrentPage = job.StartPage;
                int endPage = job.EndPage;
                int limit = job.Limit;
                bool flag = true;

                job.DownloadCount = 0;

                while (flag)
                {
                    if (isJobCancelled(job)) return;

                    job.Message = "Parsing search page: " + job.CurrentPage;
                    var option = new NijieSearchOption()
                    {
                        Query = job.SearchTag,
                        Page = job.CurrentPage,
                        Sort = job.Sort,
                        Matching = job.Matching,
                        SearchBy = job.SearchBy
                    };
                    var searchPage = MainWindow.Bot.Search(option);

                    foreach (var image in searchPage.Images)
                    {
                        if (isJobCancelled(job)) return;

                        if (image.IsFriendOnly || image.IsGoldenMember)
                        {
                            job.Message = String.Format("Skipping ImageId: {0} because locked", image.ImageId);
                            continue;
                        }

                        processImage(job, null, image);
                        ++job.DownloadCount;
                        if (job.DownloadCount > limit && limit != 0)
                        {
                            job.Message = "Image limit reached: " + limit;
                            return;
                        }
                    }

                    ++job.CurrentPage;
                    if (job.CurrentPage > endPage && endPage != 0)
                    {
                        job.Message = "Page limit reached: " + endPage;
                        return;
                    }
                    else if (job.DownloadCount < limit)
                    {
                        flag = searchPage.IsNextAvailable;
                    }
                }
            }
            catch (NijieException ne)
            {
                HandleJobException(job, ne);
                MainWindow.Log.Error("Error when processing Search Job: " + job.Name, ne);
            }
        }

        /// <summary>
        /// Process images from member page.
        /// </summary>
        /// <param name="job"></param>
        private static void doMemberJob(JobDownloadViewModel job)
        {
            if (isJobCancelled(job)) return;

            MainWindow.Log.Debug("Running Member Job: " + job.Name);
            try
            {
                job.Message = "Parsing member page";
                var memberPage = MainWindow.Bot.ParseMember(job.MemberId);

                if (Properties.Settings.Default.DownloadAvatar)
                {
                    var rootPath = Properties.Settings.Default.RootDirectory;
                    var avatarFilename = MainWindow.makeFilename(job, new NijieImage() { Member = memberPage }, type: MainWindow.FilenameFormatType.Avatar);
                    downloadUrl(job, memberPage.AvatarUrl, memberPage.MemberUrl, rootPath + Path.DirectorySeparatorChar + avatarFilename);
                }

                foreach (var imageTemp in memberPage.Images)
                {
                    if (isJobCancelled(job)) return;

                    processImage(job, memberPage, imageTemp);
                    ++job.DownloadCount;

                    if (job.CurrentPage > job.EndPage && job.EndPage != 0)
                    {
                        job.Message = "Page limit reached: " + job.EndPage;
                        return;
                    }
                    else if (job.DownloadCount > job.Limit && job.Limit != 0)
                    {
                        job.Message = "Image limit reached: " + job.Limit;
                        return;
                    }
                }
            }
            catch (NijieException ne)
            {
                HandleJobException(job, ne);
                MainWindow.Log.Error("Error when processing Member Job: " + job.Name, ne);
            }
        }

        private static bool isJobCancelled(JobDownloadViewModel job)
        {
            lock (_lock)
            {
                if (job.CancelToken.IsCancellationRequested && job.Status != JobStatus.Completed && job.Status != JobStatus.Error)
                {
                    job.Status = JobStatus.Cancelled;
                    job.Message = "Job Cancelled.";
                    MainWindow.Log.Debug(string.Format("Job: {0} cancelled", job.Name));
                    return true;
                }
                return false;
            }
        }

        #endregion main job method.

        #region actual download image related

        /// <summary>
        /// Parse the image page
        /// - Illustration
        /// - Manga
        /// </summary>
        /// <param name="job"></param>
        /// <param name="memberPage"></param>
        /// <param name="imageTemp"></param>
        private static void processImage(JobDownloadViewModel job, NijieMember memberPage, NijieImage imageTemp)
        {
            if (isJobCancelled(job))
                return;

            MainWindow.Log.Debug("Processing Image:" + imageTemp.ImageId);

            // skip if exists in DB
            if (Properties.Settings.Default.SkipIfExistsInDB && !Properties.Settings.Default.Overwrite)
            {
                using (var dao = new NijieContext())
                {
                    var result = NijieImage.IsDownloadedInDB(imageTemp.ImageId);
                    if (result)
                    {
                        job.Message = String.Format("Image {0} already downloaded in DB", imageTemp.ImageId);
                        return;
                    }
                }
            }

            var image = MainWindow.Bot.ParseImage(imageTemp, memberPage);

            if (image.IsFriendOnly)
            {
                // sample: 74587
                job.Message = "Image locked!";
                return;
            }
            if (image.IsGoldenMember)
            {
                job.Message = "Image only for Gold Membership";
                return;
            }

            if (image.IsManga)
            {
                processManga(job, image);
            }
            else
            {
                processIllustration(job, image);
            }
        }

        private static void processIllustration(JobDownloadViewModel job, NijieImage image)
        {
            var result = false;
            if (isJobCancelled(job))
                return;
            job.PauseEvent.WaitOne(Timeout.Infinite);

            var filename = MainWindow.makeFilename(job, image, type: MainWindow.FilenameFormatType.Image);
            job.Message = "Downloading: " + image.BigImageUrl;
            filename = filename + "." + Util.ParseExtension(image.BigImageUrl);
            filename = Properties.Settings.Default.RootDirectory + Path.DirectorySeparatorChar + Util.SanitizeFilename(filename);
            if (canDownloadFile(job, image.BigImageUrl, filename))
            {
                result = downloadUrl(job, image.BigImageUrl, image.ViewUrl, filename);
                image.SavedFilename = filename;
                image.ServerFilename = Util.ExtractFilenameFromUrl(image.BigImageUrl);
                image.Filesize = new FileInfo(filename).Length;
            }

            if (Properties.Settings.Default.SaveDB && result)
                SaveImageToDB(job, image);
        }

        private static void processManga(JobDownloadViewModel job, NijieImage image)
        {
            var downloaded = false;
            MainWindow.Log.Debug("Processing Manga Images:" + image.ImageId);
            string lastFilename = "", lastUrl = "";
            for (int i = 0; i < image.ImageUrls.Count; ++i)
            {
                downloaded = false;
                if (isJobCancelled(job))
                    return;
                job.PauseEvent.WaitOne(Timeout.Infinite);

                var filename = MainWindow.makeFilename(job, image, i, MainWindow.FilenameFormatType.Manga);
                job.Message = "Downloading: " + image.ImageUrls[i];
                var pagefilename = filename;
                if (!(job.SaveFilenameFormat.Contains(MainWindow.FILENAME_FORMAT_PAGE) || job.SaveFilenameFormat.Contains(MainWindow.FILENAME_FORMAT_PAGE_ZERO)))
                {
                    pagefilename += "_p" + i;
                }
                pagefilename += "." + Util.ParseExtension(image.ImageUrls[i]);
                pagefilename = Properties.Settings.Default.RootDirectory + Path.DirectorySeparatorChar + Util.SanitizeFilename(pagefilename);

                var pages = image.MangaPages as List<NijieMangaInfo>;
                if (canDownloadFile(job, image.ImageUrls[i], pagefilename))
                {
                    downloaded = downloadUrl(job, image.ImageUrls[i], image.Referer, pagefilename);
                    pages[i].SavedFilename = pagefilename;
                    pages[i].ServerFilename = Util.ExtractFilenameFromUrl(image.ImageUrls[i]);
                    pages[i].Filesize = new FileInfo(pagefilename).Length;
                }

                lastFilename = pagefilename;
                lastUrl = image.ImageUrls[i];
            }
            image.SavedFilename = lastFilename;
            image.ServerFilename = Util.ExtractFilenameFromUrl(lastUrl);
            image.Filesize = new FileInfo(lastFilename).Length;

            if (Properties.Settings.Default.SaveDB && downloaded)
                SaveImageToDB(job, image);
        }

        private static void SaveImageToDB(JobDownloadViewModel job, NijieImage image)
        {
            try
            {
                lock (_dbLock)
                {
                    using (var dao = new NijieContext())
                    {
                        if (Properties.Settings.Default.TraceDB)
                        {
                            dao.Database.Log = MainWindow.Log.Debug;
                        }
                        image.SaveToDb(dao);
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log.Error("Failed to save to DB: " + image.ImageId, ex);
                job.Message += ex.Message;
            }
        }

        /// <summary>
        /// Check if the url can be downloaded based on overwrite setting and existing file.
        /// </summary>
        /// <param name="job"></param>
        /// <param name="url"></param>
        /// <param name="filename"></param>
        /// <returns></returns>
        private static bool canDownloadFile(JobDownloadViewModel job, String url, String filename)
        {
            if (!File.Exists(filename) || Properties.Settings.Default.Overwrite)
            {
                if (File.Exists(filename))
                {
                    MainWindow.Log.Debug("Overwriting file: " + filename);
                    job.Message = "Overwriting file: " + filename;
                }
                return true;
            }
            else
            {
                MainWindow.Log.Debug(String.Format("Skipping url: {0}, file {1} exists: ", url, filename));
                job.Message = "Skipped, file exists: " + filename;

                return false;
            }
        }

        /// <summary>
        /// Download the image to the specified filename from the Job.
        /// </summary>
        /// <param name="job"></param>
        /// <param name="url"></param>
        /// <param name="referer"></param>
        /// <param name="filename"></param>
        private static bool downloadUrl(JobDownloadViewModel job, string url, string referer, string filename)
        {
            filename = Util.SanitizeFilename(filename);
            url = Util.FixUrl(url);

            MainWindow.Log.Debug(String.Format("Downloading url: {0} ==> {1}", url, filename));
            int retry = 0;
            while (retry < 3)
            {
                if (isJobCancelled(job))
                    return false;

                try
                {
                    job.Message = "Saving to: " + filename;
                    MainWindow.Bot.Download(url, referer, filename,
                                            Properties.Settings.Default.Overwrite,
                                            Properties.Settings.Default.OverwriteOnlyIfDifferentSize,
                                            Properties.Settings.Default.MakeBackupOldFile
                                            , x =>
                                            {
                                                job.Message = x;
                                            });
                    break;
                }
                catch (NijieException nex)
                {
                    job.Message = Util.GetAllInnerExceptionMessage(nex);
                    MainWindow.Log.Error(nex.Message);

                    var webException = nex.InnerException as System.Net.WebException;
                    if (webException != null)
                    {
                        var response = webException.Response as System.Net.HttpWebResponse;
                        if (response.StatusCode == System.Net.HttpStatusCode.GatewayTimeout ||
                            response.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
                        {
                            ++retry;
                            MainWindow.Log.Error(String.Format("Failed to download url: {0}, retrying {1} of {2}", url, retry, 3), webException);
                            for (int i = 0; i < Properties.Settings.Default.RetryDelay; ++i)
                            {
                                job.Message = webException.Message + " retry: " + retry + " wait: " + i;
                                Thread.Sleep(1000);
                                if (job.CancelToken.IsCancellationRequested)
                                {
                                    addException(job, nex, url, filename);
                                    return false;
                                }
                            }
                            continue;
                        }
                    }

                    addException(job, nex, url, filename);
                    return false;
                }
                catch (Exception ex)
                {
                    ++retry;
                    MainWindow.Log.Error(String.Format("Failed to download url: {0}, retrying {1} of {2}", url, retry, 3), ex);
                    for (int i = 0; i < Properties.Settings.Default.RetryDelay; ++i)
                    {
                        job.Message = ex.Message + " retry: " + retry + " wait: " + i;
                        Thread.Sleep(1000);
                        if (job.CancelToken.IsCancellationRequested)
                        {
                            var nex = new NijieException(ex.Message, ex, NijieException.DOWNLOAD_ERROR);
                            addException(job, nex, url, filename);
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        private static void addException(JobDownloadViewModel job, NijieException nex, string url, string filename)
        {
            Application.Current.Dispatcher.BeginInvoke(
                 new Action<BatchDownloadPage>((y) =>
                 {
                     if (nex.ErrorCode != NijieException.DOWNLOAD_SKIPPED)
                     {
                         nex.Url = url;
                         nex.Filename = filename;
                         job.Exceptions.Add(nex);
                     }
                 }),
                 new object[] { null }
              );
        }

        #endregion actual download image related

        private static void HandleJobException(JobDownloadViewModel job, NijieException ne)
        {
            job.Status = JobStatus.Error;
            job.Message = Util.GetAllInnerExceptionMessage(ne);
        }
    }
}