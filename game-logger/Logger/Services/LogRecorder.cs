﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.S3;
using Amazon.S3.Transfer;
using Domain.Models;
using Domain.Models.DTOs;
using Domain.Services;
using Logger.Enums;
using Logger.Interfaces;
using Logger.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Linq;

namespace Logger.Services
{
    public class LogRecorder : ILogRecorderService
    {
        private readonly LoggerConfig loggerConfig;
        private HubConnection connection;
        private readonly List<GameException> gameExceptionLog;
        private readonly List<GameStateDto> gameStateDtoLog;
        private readonly List<BotDto> botDtoLog;
        private readonly bool verboseLogging;
        private readonly string? matchStatusFileName;
        private readonly string? gameCompleteFileName;
        private readonly string logDirectory;
        private bool waitingForSideCar;
        private bool calledForDisconnect;
        private readonly string s3BucketName;
        private readonly string s3BucketKey;
        private readonly string awsRegion;
        private static IAmazonS3 s3Client;
        private readonly bool pushLogsToS3;
        private bool running = false;
        private string runnerUrl;
        private bool logsSaving = false;
        private bool logsSaved = false;

        public LogRecorder(IOptions<LoggerConfig> loggerConfig)
        {
            this.loggerConfig = loggerConfig.Value;
            gameStateDtoLog = new List<GameStateDto>();
            botDtoLog = new List<BotDto>();
            gameExceptionLog = new List<GameException>();
            var logEnvar = Environment.GetEnvironmentVariable("VERBOSE_LOGGING");
            verboseLogging = !string.IsNullOrWhiteSpace(logEnvar) && logEnvar.Equals("true", StringComparison.InvariantCultureIgnoreCase);

            matchStatusFileName = Environment.GetEnvironmentVariable("MATCH_STATUS_FILE");
            gameCompleteFileName = Environment.GetEnvironmentVariable("GAME_COMPLETE_FILE");

            logDirectory = Environment.GetEnvironmentVariable("LOG_DIRECTORY") ?? this.loggerConfig.LogDirectory;

            s3BucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
            s3BucketKey = Environment.GetEnvironmentVariable("S3_BUCKET_KEY");
            awsRegion = Environment.GetEnvironmentVariable("AWS_REGION");

            pushLogsToS3 = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PUSH_LOGS_TO_S3"));
        }

        public async Task Startup()
        {
            LogWriter.LogInfo("Logger", "Starting up");
            var environmentIp = Environment.GetEnvironmentVariable("RUNNER_IPV4");
            var url = !string.IsNullOrWhiteSpace(environmentIp) ? environmentIp : loggerConfig.RunnerUrl;
            runnerUrl = url.StartsWith("http://") ? url : "http://" + url;
            runnerUrl += ":" + loggerConfig.RunnerPort;

            var canSeeRunner = false;
            using (var httpClient = new HttpClient())
            {
                while (!canSeeRunner)
                {
                    LogWriter.LogDebug("Core.Startup", "Testing network visibility of Runner");
                    LogWriter.LogDebug("Core.Startup", $"Testing URL: {runnerUrl}");
                    try
                    {
                        var result = await httpClient.GetAsync($"{runnerUrl}/api/health/runner");
                        if (result.StatusCode != HttpStatusCode.OK)
                        {
                            LogWriter.LogDebug(
                                "Core.Startup",
                                $"Can not see runner at {runnerUrl}/api/health/runner. Waiting 1 second and trying again.");
                            Thread.Sleep(1000);
                            continue;
                        }
                    }
                    catch (Exception e)
                    {
                        LogWriter.LogDebug(
                            "Core.Startup",
                            $"Can not see runner at {runnerUrl}/api/health/runner. Waiting 1 second and trying again.");
                        Thread.Sleep(1000);
                        continue;
                    }

                    LogWriter.LogDebug("Core.Startup", $"Can see runner at {runnerUrl}");
                    canSeeRunner = true;
                }
            }

            LogWriter.LogDebug("SignalR.Startup", $"Connecting SignalR to {runnerUrl}");

            connection = new HubConnectionBuilder().WithUrl($"{runnerUrl}/runnerhub").Build();
            await connection.StartAsync()
                .ContinueWith(
                    async task =>
                    {
                        Console.WriteLine("Connected");

                        await connection.SendAsync("RegisterGameLogger");

                        /* Disconnect Request Handler */
                        connection.On<Guid>("Disconnect", OnDisconnect);

                        connection.On<GameStateDto>("ReceiveGameState", OnWriteGameStateLog);

                        //connection.On<BotDto>("ReceiveBotState", OnWriteBotDtoLog); 

                        connection.On<GameException>("WriteExceptionLog", OnWriteExceptionLog);

                        connection.On<GameCompletePayload>("SaveLogs", OnSaveLogs);
                        connection.On<string>("ReceiveGameComplete", OnGameComplete);
                        running = true;
                        await GameLog();
                    });
        }

        private async Task OnGameComplete(string obj)
        {
            var payload = JsonConvert.DeserializeObject<GameCompletePayload>(obj);
            await OnSaveLogs(payload);
        }

        private async Task GameLog()
        {
            while (running)
            {
                if (connection.State == HubConnectionState.Connected)
                {
                    continue;
                }

                if (calledForDisconnect)
                {
                    continue;
                }

                LogWriter.LogError("GameLog", "Runner disconnected. Informing Runner of failed connection.");
                running = false;
                await InformFailedConnection();
                break;
            }
        }

        private async Task InformFailedConnection()
        {
            using var httpClient = new HttpClient();
            try
            {
                var connectionInformation = new ConnectionInformation { Reason = "Logger was disconnected due to failed signalR connection", Status = ConnectionStatus.Disconnected };
                var content = new StringContent(JsonConvert.SerializeObject(connectionInformation), Encoding.UTF8, "application/json");
                var result = await httpClient.PostAsync($"{runnerUrl}/api/connections/logger", content);
                if (result.StatusCode != HttpStatusCode.OK)
                {
                    LogWriter.LogError(
                        "Shutdown",
                        $"Tried to inform runner of a disconnect but could not reach the runner.");
                }
            }
            catch (Exception e)
            {
                LogWriter.LogDebug(
                    "Shutdown",
                    $"Tried to inform runner of a disconnect but could not reach the runner.");
            }
            StopApplication();
        }

        private async Task OnWriteGameStateLog(GameStateDto gameStateDto)
        {

            //Console.WriteLine(JsonConvert.SerializeObject(gameStateDto, Formatting.Indented));

            LogWriter.LogInfo("Logger", $"Tick: {gameStateDto.World.CurrentTick}, Adding to game state log");

            // add to the static dto before this point/ or filter out all static values
            gameStateDtoLog.Add(gameStateDto);
            if (!verboseLogging)
            {
                return;
            }

            var filePath = WriteFileWithSerialisation(
                $"{gameStateDto.World.CurrentTick}_DTO_{loggerConfig.GameStateLogFileName}",
                logDirectory,
                gameStateDtoLog.ToArray());
        }


        private async Task OnWriteStaticStateLog(GameStateDto gameStateDto)
        {

            //Console.WriteLine(JsonConvert.SerializeObject(gameStateDto, Formatting.Indented));

            LogWriter.LogInfo("Logger", $"Tick: {gameStateDto.World.CurrentTick}, Adding to static state log");
            gameStateDtoLog.Add(gameStateDto);
            if (!verboseLogging)
            {
                return;
            }

            var filePath = WriteFileWithSerialisation(
                $"{gameStateDto.World.CurrentTick}_DTO_{loggerConfig.GameStateLogFileName}",
                logDirectory,
                gameStateDtoLog.ToArray());
        }

        private async Task OnWriteBotDtoLog(BotDto botDto)
        {

            //Console.WriteLine(JsonConvert.SerializeObject(gameStateDto, Formatting.Indented));

            LogWriter.LogInfo("Logger", $"Tick: {botDto.Tick}, Adding to bot state log");

            botDtoLog.Add(botDto);
            if (!verboseLogging)
            {
                return;
            }

            if (botDto.Tick % 100 != 0)
            {
                return;
            }

            var filePath = WriteFileWithSerialisation(
                $"{botDto.Tick}_DTO_{loggerConfig.GameStateLogFileName}",
                logDirectory,
                botDtoLog.ToArray());
        }

        private void OnWriteExceptionLog(GameException gameException)
        {
            LogWriter.LogInfo("Logger", $"Adding GameException {gameException.CurrentTick} to log...");
            // Only add the current game state to logs.
            gameExceptionLog.Add(gameException);
            if (gameExceptionLog.Count <= 0)
            {
                return;
            }

            WriteFileWithSerialisation(loggerConfig.GameExceptionLogFileName, logDirectory, gameExceptionLog.ToArray());
        }

        private async Task OnSaveLogs(GameCompletePayload gameCompletePayload)
        {
            if (logsSaved || logsSaving)
            {
                return;
            }

            logsSaving = true;

            LogWriter.LogInfo("Logger", "Game Complete. Saving Logs...");

            var finalLogDir = logDirectory;

            if (pushLogsToS3)
            {
                finalLogDir = $"{logDirectory}/logs";
                var finalLogDirDetails = Directory.CreateDirectory(finalLogDir);
            }

            LogWriter.LogInfo("Logger", $"Saving Files into Directory: {finalLogDir}, Current Directory: {Directory.GetCurrentDirectory()}");

            string gameExceptionFilePath = null;
            if (gameExceptionLog.Count > 0)
            {
                LogWriter.LogInfo("Logger", "Game Exception Log");
                gameExceptionFilePath = WriteFileWithSerialisation(loggerConfig.GameExceptionLogFileName,
                    finalLogDir,
                    gameExceptionLog.ToArray());
            }

            var logTime = $"{DateTime.Now:yyyy-MM-dd_hh-mm-ss}";

            //Log game state
            var stateFileName = matchStatusFileName ?? $"{loggerConfig.GameStateLogFileName}_{logTime}";
            var condencedLofFileName = matchStatusFileName ?? $"GameStateCondencedLog_{logTime}";

            WriteFileWithSerialisation(
                condencedLofFileName,
                finalLogDir,
                loggerConfig.CondencedLoggingToggle ? VariableGameLog() : FinalGameLog());

            if (loggerConfig.CondencedLoggingToggle)
            {
                //Log static state
                var staticStateFileName = matchStatusFileName ?? $"{loggerConfig.GameStateStaticLogFileName}_{logTime}";

                WriteFileWithSerialisation(
                    staticStateFileName,
                    finalLogDir,
                    StaticGameLog());
            }



            //Log game complete
            WriteFileWithSerialisation(
                $"{gameCompleteFileName ?? $"{loggerConfig.GameStateLogFileName}_{logTime}_GameComplete"}",
                finalLogDir,
                gameCompletePayload);


            LogWriter.LogInfo("Logger", "Logs Saved Successfully");

            LogWriter.LogInfo("Logger", $"Log Directory: {logDirectory}, Current Directory: {Directory.GetCurrentDirectory()}");

            // Push to aws
            if (pushLogsToS3)
            {
                try
                {
                    await SaveLogsToS3(finalLogDir);
                }
                catch (Exception e)
                {
                    LogWriter.LogError("AWS.S3", $"Upload failed with reason: {e.Message}");
                }
            }

            var sideCarTimeout = Environment.GetEnvironmentVariable("WAIT_FOR_SIDECAR");
            LogWriter.LogInfo(
                "Logger",
                $"Waiting for sidecar: {!string.IsNullOrWhiteSpace(sideCarTimeout)}, Waiting for: {sideCarTimeout}ms");

            if (string.IsNullOrWhiteSpace(sideCarTimeout))
            {
                await connection.InvokeAsync("GameLoggingComplete");

                waitingForSideCar = false;
                return;
            }

            waitingForSideCar = true;
            Thread.Sleep(int.Parse(sideCarTimeout));
            LogWriter.LogInfo("Logger", "Completed timeout for Sidecar");
            if (!calledForDisconnect)
            {
                return;
            }

            await connection.InvokeAsync("GameLoggingComplete");

            waitingForSideCar = false;
            logsSaved = true;
        }

        private object FinalGameLog()
        {
            List<object> logList = new List<object>();

            foreach (var log in gameStateDtoLog)
            {
                logList.Add(log);

                var botLogs = botDtoLog.Where(x => x.Tick == log.World.CurrentTick).ToList();

                logList.AddRange(botLogs);
            }

            return logList;
        }
        private object StaticGameLog()
        {
            List<object> staticLogList = new List<object>();

            staticLogList.Add(GameStateDto.GetStaticFields(gameStateDtoLog.First()));

            return staticLogList;
        }

        private object VariableGameLog()
        {
            List<object> variableLogList = new List<object>();

            for (int i = 1; i < gameStateDtoLog.Count; i++)
            {
                GameStateDto variableLog = GameStateDto.GetVariableFields(gameStateDtoLog[dec(i)], gameStateDtoLog[i]);
                variableLogList.Add(variableLog);
            }

            return variableLogList;
        }

        private int dec(int i)
        {
            return i -= 1;
        }

        private string WriteFileWithSerialisation(
            string logFileName,
            string dir,
            object objToSerialise)
        {
            var filename = logFileName.EndsWith(".json") ? logFileName : $"{logFileName}.json";
            var path = Path.Combine(dir, filename);

            LogWriter.LogInfo("Logger", $"Begin writing file {path.ToString()}");
            try
            {
                using var file = File.CreateText(path);
                var serializer = new JsonSerializer
                {
                    NullValueHandling = NullValueHandling.Ignore,
                };
                try
                {
                    serializer.Serialize(file, objToSerialise);
                    file.Close();
                }
                catch (Exception e)
                {
                    LogWriter.LogInfo("Logger", $"Serializer Error: {e.Message}");
                }
            }
            catch (Exception e)
            {
                LogWriter.LogInfo("Logger", $"Error: {e.Message}");
            }
            LogWriter.LogInfo("Logger", $"Finished writing file {path.ToString()}");
            return path;
        }

        private async Task OnDisconnect(Guid id)
        {
            if (waitingForSideCar)
            {
                LogWriter.LogWarning("Disconnected", "Runner Invoked Disconnect, but waiting for sidecar before shutting down.");
                calledForDisconnect = true;
                return;
            }

            LogWriter.LogInfo("Disconnected", "Runner Invoked Disconnect");
            running = false;
            await connection.StopAsync();
            StopApplication();
        }

        private void StopApplication()
        {
            try
            {
                LogWriter.LogInfo("Shutdown", "Shutting down Logger");
                connection.StopAsync();
            }
            finally
            {
                Program.CloseApplication();
            }
        }

        private async Task SaveLogsToS3(string finalLogDir)
        {
            var bucketName = s3BucketName;
            var bucketKey = s3BucketKey;
            var bucketRegion = RegionEndpoint.GetBySystemName(awsRegion);

            LogWriter.LogInfo("AWS.S3", "Beginning S3 Upload");
            s3Client = new AmazonS3Client(bucketRegion);
            var transferUtility = new TransferUtility(s3Client);
            await transferUtility.UploadDirectoryAsync(finalLogDir, bucketName);
            LogWriter.LogInfo("AWS.S3", "Completed S3 Upload");
        }

        private async Task UploadFileAsync(string filePath, string bucketName, string keyName)
        {
            try
            {
                var fileTransferUtility = new TransferUtility(s3Client);
                await fileTransferUtility.UploadAsync(filePath, bucketName, keyName);
                LogWriter.LogInfo("S3_Logs", "Successfully uploaded file to s3");
                Console.WriteLine("");
            }
            catch (AmazonS3Exception e)
            {
                LogWriter.LogError("S3_Logs", ($"S3 error uploading file. Message:'{e.Message}' when uploading an object"));
            }
            catch (Exception e)
            {
                LogWriter.LogError("S3_Logs", $"Unknown error uploading file. Message:'{e.Message}' when uploading an object");
            }
        }
    }
}