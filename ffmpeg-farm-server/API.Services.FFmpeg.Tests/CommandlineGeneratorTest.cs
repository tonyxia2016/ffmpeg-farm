﻿using System;
using System.Drawing;
using System.Runtime.Serialization.Formatters;
using Contract;
using NUnit.Framework;

namespace API.Services.FFmpeg.Tests
{
    [TestFixture]
    public class CommandlineGeneratorTest
    {
        [Test]
        public void Get_ShouldStartWithInput()
        {
            // Arrange
            const string inputFilename = "test input filename";
            var parameters = new FFmpegParameters
            {
                Inputfile = inputFilename
            };

            // Act
            var result = CommandlineGenerator.Get(parameters);

            // Assert
            StringAssert.StartsWith($@"-i ""{inputFilename}", result);
        }

        [Test]
        public void Get_ShouldSetAudioCodecAndBitrate()
        {
            // Arrange
            const string inputFilename = "test input filename";
            var parameters = GetTestParameters(inputFilename, audioCodec: AudioCodec.AAC, audioBitrate: 128000);

            // Act
            var result = CommandlineGenerator.Get(parameters);

            // Assert
            string expected =
                $@"-i ""{inputFilename}"" -codec:a {parameters.AudioParam.Codec.ToString().ToLower()} -b:a {
                        parameters.AudioParam.Bitrate
                    }k";

            Assert.That(result, Is.EqualTo(expected));
        }

        [Test]
        public void Get_ShouldSetDeinterlaceFilter()
        {
            // Arrange
            const string inputFilename = "test input filename";
            var parameters = GetTestParameters(inputFilename,
                FFmpegParameters.DeinterlaceSettings.DeinterlaceMode.SendFrame,
                FFmpegParameters.DeinterlaceSettings.DeinterlaceParity.Auto, true);

            parameters.VideoParam = null;
            parameters.AudioParam = new FFmpegParameters.Audio
            {
                Codec = AudioCodec.AAC,
                Bitrate = 128000
            };
            parameters.Deinterlace = new FFmpegParameters.DeinterlaceSettings
            {
                Mode = FFmpegParameters.DeinterlaceSettings.DeinterlaceMode.SendFrame,
                Parity = FFmpegParameters.DeinterlaceSettings.DeinterlaceParity.Auto,
                DeinterlaceAllFrames = true
            };

            // Act
            var result = CommandlineGenerator.Get(parameters);

            // Assert
            string expected =
                $@"-i ""{inputFilename}"" -filter_complex ""yadif=0:-1:0"" -codec:a {
                        parameters.AudioParam.Codec.ToString().ToLower()
                    } -b:a {parameters.AudioParam.Bitrate}k";
            Assert.That(result, Is.EqualTo(expected));
        }

        [Test]
        public void Get_ShouldSetVideoCodec()
        {
            // Arrange
            const string inputFilename = "test input filename";
            var parameters = GetTestParameters(inputFilename, videoBitrate: 1024000, videoCodec: VideoCodec.LibX264, videoPreset: "medium");

            // Act
            var result = CommandlineGenerator.Get(parameters);

            // Assert
            string expected = $@"-i ""{inputFilename}"" -codec:v libx264 -preset medium -b:v 1000k";
            Assert.That(result, Is.EqualTo(expected));
        }

        [Test]
        [TestCase(1920, 1080)]
        [TestCase(1280, 720)]
        [TestCase(1024, 480)]
        [TestCase(1024, 576)]
        [TestCase(720, 576)]
        [TestCase(720, 480)]
        [TestCase(640, 360)]
        public void Get_ShouldSetResizeInfo(int width, int height)
        {
            // Arrange
            const string inputFilename = "test input filename";

            var parameters = GetTestParameters(inputFilename, videoCodec: VideoCodec.LibX264, videoBitrate: 1024000,
                videoSize: new VideoSize(width, height));

            // Act
            var result = CommandlineGenerator.Get(parameters);

            // Assert
            Assert.That(result, Is.EqualTo($@"-i ""{inputFilename}"" -filter_complex ""scale={width}:{height}"" -codec:v libx264 -preset medium -b:v 1000k"));
        }

        [Test]
        public void Get_ShouldSetBothAudioAndVideoInfo()
        {
            // Arrange
            const string inputFilename = "test input filename";
            var parameters = GetTestParameters(inputFilename,
                FFmpegParameters.DeinterlaceSettings.DeinterlaceMode.SendFrame,
                FFmpegParameters.DeinterlaceSettings.DeinterlaceParity.Auto, true, 1024000, VideoCodec.LibX264);

            // Act
            var result = CommandlineGenerator.Get(parameters);

            // Assert
            string expected = $@"-i ""{inputFilename}"" -filter_complex ""yadif=0:-1:0"" -codec:v libx264 -preset medium -b:v 1000k";
            Assert.That(result, Is.EqualTo(expected));
        }

        private static FFmpegParameters GetTestParameters(string inputFilename,
            FFmpegParameters.DeinterlaceSettings.DeinterlaceMode deinterlaceMode =
                FFmpegParameters.DeinterlaceSettings.DeinterlaceMode.Unknown,
            FFmpegParameters.DeinterlaceSettings.DeinterlaceParity deinterlaceParity =
                FFmpegParameters.DeinterlaceSettings.DeinterlaceParity.Unknown, bool deinterlaceAllFrames = false,
            int videoBitrate = 0, VideoCodec videoCodec = VideoCodec.Unknown, string videoPreset = "",
            AudioCodec audioCodec = AudioCodec.Unknown, int audioBitrate = 0, VideoSize videoSize = null)
        {
            if (string.IsNullOrWhiteSpace(inputFilename)) throw new ArgumentNullException(nameof(inputFilename));

            var parameters = new FFmpegParameters
            {
                Inputfile = inputFilename
            };

            if (videoCodec != VideoCodec.Unknown)
            {
                if (parameters.VideoParam == null)
                {
                    parameters.VideoParam = new FFmpegParameters.Video();
                }

                parameters.VideoParam.Codec = videoCodec;
            }
            if (videoBitrate > 0)
            {
                if (parameters.VideoParam == null)
                {
                    parameters.VideoParam = new FFmpegParameters.Video();
                }

                parameters.VideoParam.Bitrate = videoBitrate;
            }

            if (audioCodec != AudioCodec.Unknown)
            {
                if (parameters.AudioParam == null)
                {
                    parameters.AudioParam = new FFmpegParameters.Audio();
                }

                parameters.AudioParam.Codec = audioCodec;
            }

            if (audioBitrate > 0)
            {
                if (parameters.AudioParam == null)
                {
                    parameters.AudioParam = new FFmpegParameters.Audio();
                }

                parameters.AudioParam.Bitrate = 128000;
            }

            if (deinterlaceMode != FFmpegParameters.DeinterlaceSettings.DeinterlaceMode.Unknown && deinterlaceParity !=
                FFmpegParameters.DeinterlaceSettings.DeinterlaceParity.Unknown)
            {
                parameters.Deinterlace = new FFmpegParameters.DeinterlaceSettings
                {
                    Mode = deinterlaceMode,
                    Parity = deinterlaceParity,
                    DeinterlaceAllFrames = deinterlaceAllFrames
                };
            }

            if (videoSize != null)
            {
                parameters.VideoParam.Size = videoSize;
            }

            if (!string.IsNullOrWhiteSpace(videoPreset))
            {
                parameters.VideoParam.Preset = videoPreset;
            }

            return parameters;
        }
    }
}