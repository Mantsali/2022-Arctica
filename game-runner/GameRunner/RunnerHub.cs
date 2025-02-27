using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using Domain.Models;
using Domain.Models.DTOs;
using Domain.Services;
using Engine.Models;
using GameRunner.Enums;
using GameRunner.Interfaces;
using GameRunner.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace GameRunner
{
    public class RunnerHub : Hub
    {
        private readonly IRunnerStateService runnerStateService;
        private readonly RunnerConfig runnerConfig;
        private Timer componentTimer;
        private readonly IHostApplicationLifetime applicationLifetime;
        private readonly ICloudIntegrationService cloudIntegrationService;

        public RunnerHub(
            IRunnerStateService runnerStateService,
            IConfigurationService runnerConfig,
            IHostApplicationLifetime appLifetime,
            ICloudIntegrationService cloudIntegrationService)
        {
            this.runnerStateService = runnerStateService;
            this.runnerConfig = runnerConfig.RunnerConfig;
            applicationLifetime = appLifetime;
            this.cloudIntegrationService = cloudIntegrationService;
        }

        #region Runner endpoints

        /// <summary>
        ///     Register a bot on connect.
        /// </summary>
        /// <returns></returns>
        public override async Task OnConnectedAsync()
        {
            Logger.LogDebug("RunnerHub", "New Connection");
            await base.OnConnectedAsync();
        }

        /// <summary>
        ///     Deregister a bot on disconnect.
        /// </summary>
        /// <param name="exception"></param>
        /// <returns></returns>
        public override Task OnDisconnectedAsync(Exception exception)
        {
            Logger.LogDebug("DisconnectEvent", exception?.Message);
            runnerStateService.DeregisterBot(Context.ConnectionId);
            Groups.RemoveFromGroupAsync(Context.ConnectionId, "players");

            var result = base.OnDisconnectedAsync(exception);
            return result;
        }

        #endregion

        #region Game Engine endpoints

        /// <summary>
        ///     Register game engine on runner.
        /// </summary>
        /// <returns></returns>
        public async Task RegisterGameEngine()
        {
            runnerStateService.RegisterEngine(Context.ConnectionId, Clients.Client(Context.ConnectionId));
            if (runnerStateService.IsCoreReady)
            {
                await cloudIntegrationService.Announce(CloudCallbackType.Ready);
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, GameGroups.Components);
        }

        /// <summary>
        ///     Notify bot that it's been consumed.
        /// </summary>
        /// <param name="botId"></param>
        /// <returns></returns>
        public async Task PlayerConsumed(Guid botId)
        {
            if (runnerStateService.GetEngine().ConnectionId != Context.ConnectionId)
            {
                await SendGameException(
                    new GameException
                    {
                        ExceptionMessage = $"Invalid engine connection, botId {botId} not notified on consumed status."
                    });

                return;
            }

            var botConnectionId = runnerStateService.GetActiveConnections().FirstOrDefault(c => c.Key == botId).Value;
            runnerStateService.DeregisterBot(botConnectionId);
            Logger.LogInfo("PlayerConsumed", $"Notifying botId {botId} of consumed status.");

            if (!string.IsNullOrWhiteSpace(botConnectionId))
            {
                await Clients.Client(botConnectionId).SendAsync("ReceivePlayerConsumed");
                await Clients.Client(botConnectionId).SendAsync("Disconnect", botId);
            }
        }

        /// <summary>
        ///     Public bot state to individual bot
        /// </summary>
        /// <param name="gameStateDto"></param>
        /// <returns></returns>
        public async Task PublishBotState(GameStateDto gameStateDto)
        {
            if (runnerStateService.GetEngine().ConnectionId != Context.ConnectionId)
            {
                Logger.LogWarning("Core", "Engine endpoint invoked by unauthorized client");
                await SendGameException(
                    new GameException
                    {
                        ExceptionMessage = "Invalid engine connection, gameState not published."
                    });

                return;
            }

            var botState = gameStateDto.Bots.FirstOrDefault(bot => bot.Id == gameStateDto.BotId);

            runnerStateService.ClearBotActionsReceived();

            Logger.LogInfo("BotStatePublished", botState!.ToString());
            string connectionId = runnerStateService.GetActiveConnections()[botState.Id];

            // await runnerStateService.GetLogger().Client.SendAsync("ReceiveBotState", gameStateDto);
            await Clients.Client(connectionId).SendAsync("ReceiveBotState", gameStateDto);
        }

        /// <summary>
        ///     Public game state to all connected bots.
        /// </summary>
        /// <param name="gameStateDto"></param>
        /// <returns></returns>
        public async Task PublishGameState(GameStateDto gameStateDto)
        {
            if (runnerStateService.GetEngine().ConnectionId != Context.ConnectionId)
            {
                Logger.LogWarning("Core", "Engine endpoint invoked by unauthorized client");
                await SendGameException(
                    new GameException
                    {
                        ExceptionMessage = "Invalid engine connection, gameState not published."
                    });

                return;
            }

            // Engine sends the entire game state to the runner
            // Runner needs to send the game state to the bots

            runnerStateService.ClearBotActionsReceived();
            Logger.LogInfo(
                "GameStatePublished",
                $"Tick: {gameStateDto.World.CurrentTick}");

            await runnerStateService.GetLogger().Client.SendAsync("ReceiveGameState", gameStateDto);
            await Clients.Caller.SendAsync("TickAck", gameStateDto.World.CurrentTick);
        }

        /// <summary>
        ///     Handle GameComplete action.
        /// </summary>
        /// <returns></returns>
        public async Task GameComplete(GameCompletePayload gameCompletePayload)
        {
            if (runnerStateService.GetEngine().ConnectionId != Context.ConnectionId)
            {
                await SendGameException(
                    new GameException
                    {
                        ExceptionMessage = "Invalid engine connection, gameComplete could not be actioned."
                    });
                return;
            }

            Logger.LogInfo("RunnerHub", "Game Complete");
            runnerStateService.GameCompletePayload = gameCompletePayload;
            var completePayload = runnerStateService.GameCompletePayload;

            await Clients.All.SendAsync("ReceiveGameComplete", JsonConvert.SerializeObject(completePayload));

            var logger = runnerStateService.GetLogger();

            if (logger != default)
            {
                await logger.Client.SendAsync("SaveLogs", completePayload);
            }

            await cloudIntegrationService.Announce(CloudCallbackType.Finished);
        }

        #endregion

        #region Logger endpoints

        /// <summary>
        ///     Register game logger on runner.
        /// </summary>
        /// <returns></returns>
        public async Task RegisterGameLogger()
        {
            runnerStateService.RegisterLogger(Context.ConnectionId, Clients.Client(Context.ConnectionId));
            if (runnerStateService.IsCoreReady)
            {
                await cloudIntegrationService.Announce(CloudCallbackType.Ready);
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, GameGroups.Components);
        }

        public async Task GameLoggingComplete()
        {
            await Clients.All.SendAsync("Disconnect", new Guid());
            await cloudIntegrationService.Announce(CloudCallbackType.LoggingComplete);
            try
            {
                applicationLifetime.StopApplication();
            }
            catch (Exception e)
            {
                Logger.LogError("Shutdown", e.Message);
                applicationLifetime.StopApplication();
            }
        }

        /// <summary>
        ///     Handle GameComplete action.
        /// </summary>
        /// <returns></returns>
        public async Task SendGameException(GameException gameException)
        {
            Logger.LogDebug("GameException", gameException);
            var logger = runnerStateService.GetLogger();

            if (gameException.BotId == default)
            {
                gameException.BotId = runnerStateService.GetActiveConnections()
                    .FirstOrDefault(c => c.Value == Context.ConnectionId).Key;
            }

            if (logger != default)
            {
                await logger.Client.SendAsync("WriteExceptionLog", gameException);
            }
        }

        /// <summary>
        ///     Distributes the config values from the game engine to the bots
        /// </summary>
        /// <returns></returns>
        public async Task ReceiveConfigValues(EngineConfig config)
        {
            // send to the bot that requested 
            foreach (var (botId, connectionId) in runnerStateService.GetActiveConnections())
            {
                await Clients.Client(connectionId).SendAsync("ReceiveConfigValues", config);
            }
        }

        #endregion

        #region Bot endpoints

        /// <summary>
        ///     Allows a bot to register for a game with their given token
        /// </summary>
        /// <param name="token">Environment Token</param>
        /// <param name="nickName">NickName for bot with a max length of 12 characters</param>
        /// <returns></returns>
        public async Task Register(Guid token, string nickName)
        {
            Logger.LogInfo("Hub.Register", $"Registering Bot with nickname {nickName}");
            var botId = runnerStateService.RegisterClient(Context.ConnectionId, nickName,
                Clients.Client(Context.ConnectionId));
            if (botId == default)
            {
                Logger.LogInfo("Hub.Register", "Already reached total bot count for this match.");
                await Clients.Client(Context.ConnectionId).SendAsync("Disconnect", botId);
                return;
            }

            runnerStateService.AddRegistrationToken(Context.ConnectionId, token);
            Logger.LogDebug("Hub.Register", $"Issuing registration back to {nickName} with id {botId.ToString()}");

            await Groups.AddToGroupAsync(Context.ConnectionId, GameGroups.Players);
            await Clients.Client(Context.ConnectionId).SendAsync("Registered", botId);
            await CheckForGameStartConditions();
        }

        /// <summary>
        ///     Allow bots to send actions to Runner.
        /// </summary>
        /// <param name="playerCommand">The action the player wants to perform</param>
        /// <returns></returns>
        public async Task SendPlayerCommand(PlayerCommand playerCommand)
        {
            // TODO: Update to be in line with what is in the game engine
            // TODO: Update with simple new type player action

            if (playerCommand == null)
            {
                return;
            }

            var playerId = runnerStateService.GetBotGuidFromConnectionId(Context.ConnectionId);

            if (!playerId.HasValue ||
                runnerStateService.GetBotActionReceived(playerId.Value))
            {
                return;
            }

            runnerStateService.AddBotActionReceived(playerId.Value);
            playerCommand.PlayerId = playerId.Value;
            // TODO: Log all actions but for now just the first if it has
            var commandLog = JsonConvert.SerializeObject(playerCommand);

            Logger.LogDebug("PLAYERCOMMAND",
                $"PlayerCommand: [ command: {commandLog}, , bot: {playerCommand.PlayerId} ]");
            var engine = runnerStateService.GetEngine();
            await engine.Client.SendAsync("BotCommandReceived", playerId.Value, playerCommand);
        }

        #endregion

        #region Private methods

        private async Task CheckForGameStartConditions()
        {
            Logger.LogInfo(
                "RunnerHub",
                $"Total Clients that have Connected: {runnerStateService.TotalConnectedClients}, Active Connected Clients: {runnerStateService.TotalConnectedClients}, Target: {runnerConfig.BotCount}");

            if (runnerStateService.TotalConnections != runnerConfig.BotCount)
            {
                return;
            }

            runnerStateService.StartGame();
            await cloudIntegrationService.Announce(CloudCallbackType.Started);
        }

        #endregion
    }

    public static class GameGroups
    {
        public const string Players = "Players";
        public const string Components = "Components";
    }
}