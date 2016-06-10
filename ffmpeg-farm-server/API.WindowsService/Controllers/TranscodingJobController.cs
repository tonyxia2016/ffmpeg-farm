﻿using System;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using Contract;
using Dapper;

namespace API.WindowsService.Controllers
{
    public class TranscodingJobController : ApiController
    {
        /// <summary>
        /// Get next transcoding job
        /// </summary>
        /// <param name="machineName">Client's machine name used to stamp who took the job</param>
        /// <returns><see cref="TranscodingJob"/></returns>
        public TranscodingJob GetNextJob(string machineName)
        {
            if (string.IsNullOrWhiteSpace(machineName))
            {
                throw new HttpResponseException(new HttpResponseMessage
                {
                    ReasonPhrase = "Machinename must be specified",
                    StatusCode = HttpStatusCode.BadRequest
                });
            }

            using (var connection = Helper.GetConnection())
            {
                connection.Open();

                Helper.InsertClientHeartbeat(machineName, connection);

                int timeoutSeconds = Convert.ToInt32(ConfigurationManager.AppSettings["TimeoutSeconds"]);
                DateTime timeout =
                    DateTimeOffset.UtcNow.UtcDateTime.Subtract(TimeSpan.FromSeconds(timeoutSeconds));

                using ( var transaction = connection.BeginTransaction())
                {
                    var data = connection.Query(
                        "SELECT Id, Arguments, JobCorrelationId FROM FfmpegJobs WHERE Active = 1 AND Done = 0 AND (Taken = 0 OR HeartBeat < ?) ORDER BY Needed ASC LIMIT 1;",
                        new {timeout})
                        .FirstOrDefault();
                    if (data == null)
                        return null;

                    var rowsUpdated = connection.Execute("UPDATE FfmpegJobs SET Taken = 1, HeartBeat = ? WHERE Id = ?;",
                        new {DateTime.UtcNow, data.Id});
                    if (rowsUpdated == 0)
                        throw new Exception("Failed to mark row as taken");

                    transaction.Commit();

                    return new TranscodingJob
                    {
                        Id = Convert.ToInt32(data.Id),
                        Arguments = data.Arguments,
                        JobCorrelationId = data.JobCorrelationId
                    };
                }
            }
        }

        /// <summary>
        /// Queue new transcoding job
        /// </summary>
        /// <param name="job"></param>
        public Guid PostQueueNew(JobRequest job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (string.IsNullOrWhiteSpace(job.VideoSourceFilename) && string.IsNullOrWhiteSpace(job.AudioSourceFilename))
                throw new ArgumentException("Either VideoSourceFilename or AudioSourceFilename is a required parameter.");
            if (!string.IsNullOrWhiteSpace(job.VideoSourceFilename) && !File.Exists(job.VideoSourceFilename))
                throw new FileNotFoundException("VideoSourceFilename does not exist", job.VideoSourceFilename);
            if (!string.IsNullOrWhiteSpace(job.AudioSourceFilename) && !File.Exists(job.AudioSourceFilename))
                throw new FileNotFoundException("AudioSourceFilename does not exist", job.AudioSourceFilename);

            int duration = Helper.GetDuration(job.VideoSourceFilename);
            double framerate = Helper.GetFramerate(job.VideoSourceFilename);

            string destinationFormat = Path.GetExtension(job.DestinationFilename);
            string destinationFolder = Path.GetDirectoryName(job.DestinationFilename);
            string destinationFilenamePrefix = Path.GetFileNameWithoutExtension(job.DestinationFilename);

            if (!Directory.Exists(destinationFolder))
                throw new ArgumentException($@"Destination folder {destinationFolder} does not exist.");

            using (var connection = Helper.GetConnection())
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    const int chunkDuration = 60;

                    var jobCorrelationId = Guid.NewGuid();

                    connection.Execute(
                        "INSERT INTO FfmpegRequest (JobCorrelationId, VideoSourceFilename, AudioSourceFilename, DestinationFilename, Needed, Created, EnableDash) VALUES(?, ?, ?, ?, ?, ?, ?);",
                        new
                        {
                            jobCorrelationId,
                            job.VideoSourceFilename,
                            job.AudioSourceFilename,
                            job.DestinationFilename,
                            job.Needed,
                            DateTime.Now,
                            job.EnableDash
                        });

                    // Queue audio first because it cannot be chunked and thus will take longer to transcode
                    // and if we do it first chances are it will be ready when all the video parts are ready
                    for (int i = 0; i < job.Targets.Length; i++)
                    {
                        DestinationFormat format = job.Targets[i];

                        string chunkFilename =
                            $@"{destinationFolder}{Path.DirectorySeparatorChar}{destinationFilenamePrefix}_{i}_audio.mp4";
                        string source = job.HasAlternateAudio
                            ? job.AudioSourceFilename
                            : job.VideoSourceFilename;

                        string arguments =
                            $@"-y -i ""{source}"" -c:a aac -b:a {format.AudioBitrate}k -vn ""{chunkFilename}""";

                        const int number = 0;
                        connection.Execute(
                            "INSERT INTO FfmpegParts (JobCorrelationId, Target, Filename, Number) VALUES(?, ?, ?, ?);",
                            new {jobCorrelationId, i, chunkFilename, number});

                        connection.Execute(
                            "INSERT INTO FfmpegJobs (JobCorrelationId, Arguments, Needed, AudioSourceFilename, ChunkDuration) VALUES(?, ?, ?, ?, ?);",
                            new
                            {jobCorrelationId, arguments, job.Needed, source, duration});
                    }

                    for (int i = 0; duration - i*chunkDuration > 0; i++)
                    {
                        int value = i*chunkDuration;
                        if (value > duration)
                        {
                            value = duration;
                        }

                        string arguments =
                            $@"-y -ss {TimeSpan.FromSeconds(value)} -t {chunkDuration} -i ""{job.VideoSourceFilename}""";

                        for (int j = 0; j < job.Targets.Length; j++)
                        {
                            DestinationFormat target = job.Targets[j];

                            string chunkFilename =
                                $@"{destinationFolder}{Path.DirectorySeparatorChar}{destinationFilenamePrefix}_{j}_{value}{destinationFormat}";

                            if (job.EnableDash)
                            {
                                arguments += $@" -s {target.Width}x{target.Height} -c:v libx264 -g {framerate*4}";
                                arguments += $@" -keyint_min {framerate*4} -profile:v high -b:v {target.VideoBitrate}k";
                                arguments += $@" -level 4.1 -pix_fmt yuv420p -an ""{chunkFilename}""";
                            }
                            else
                            {
                                if (Convert.ToBoolean(ConfigurationManager.AppSettings["EnableCrf"]))
                                {
                                    int bufSize = target.VideoBitrate/8*chunkDuration;
                                    arguments += 
                                        $@" -s {target.Width}x{target.Height} -c:v libx264 -profile:v high -crf 18 -preset medium -maxrate {target
                                            .VideoBitrate}k -bufsize {bufSize}k -level 4.1 -pix_fmt yuv420p -an ""{chunkFilename}""";
                                }
                                else
                                {
                                    arguments +=
                                        $@" -s {target.Width}x{target.Height} -c:v libx264 -profile:v high -b:v {target
                                            .VideoBitrate}k -level 4.1 -pix_fmt yuv420p -an ""{chunkFilename}""";
                                }
                            }

                            connection.Execute(
                                "INSERT INTO FfmpegParts (JobCorrelationId, Target, Filename, Number) VALUES(?, ?, ?, ?);",
                                new {jobCorrelationId, j, chunkFilename, i});
                        }

                        connection.Execute(
                            "INSERT INTO FfmpegJobs (JobCorrelationId, Arguments, Needed, VideoSourceFilename, ChunkDuration) VALUES(?, ?, ?, ?, ?);",
                            new
                            {jobCorrelationId, arguments, job.Needed, job.VideoSourceFilename, chunkDuration});
                    }

                    transaction.Commit();

                    return jobCorrelationId;
                }
            }
        }

        /// <summary>
        /// Pause a job
        /// </summary>
        /// <param name="jobId">Job id</param>
        /// <returns>Number of tasks paused or zero if none were found in the queued state for the requested job</returns>
        public int PatchPause(Guid jobId)
        {
            if (jobId == Guid.Empty) throw new ArgumentException($@"Invalid Job Id specified: {jobId}");

            using (var connection = Helper.GetConnection())
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var rowsUpdated = connection.Execute(
                        "UPDATE FfmpegJobs SET Active = 0 WHERE JobCorrelationId = ? AND Done = 0 AND Taken = 0;",
                        new {jobId});

                    transaction.Commit();

                    return rowsUpdated;
                }
            }
        }
    }
}