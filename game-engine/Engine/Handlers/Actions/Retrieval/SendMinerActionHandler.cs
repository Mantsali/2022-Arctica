﻿using System;
using System.Collections.Generic;
using System.Linq;
using Domain.Enums;
using Domain.Models;
using Domain.Services;
using Engine.Extensions;
using Engine.Handlers.Interfaces;
using Engine.Interfaces;
using Engine.Models;
using Engine.Services;

namespace Engine.Handlers.Actions.Retrieval
{
    public class SendMinerActionHandler : IActionHandler
    {
        private readonly IWorldStateService worldStateService;
        private readonly ICalculationService calculationService;
        private readonly EngineConfig engineConfig;

        public SendMinerActionHandler(IWorldStateService worldStateService, IConfigurationService engineConfig,
            ICalculationService calculationService)
        {
            this.worldStateService = worldStateService;
            this.calculationService = calculationService;
            this.engineConfig = engineConfig.Value;
        }

        public bool IsApplicable(ActionType type) => type == ActionType.Mine;

        public void ProcessActionComplete(ResourceNode resourceNode, List<PlayerAction> playerActions)
        {
            // TODO: please write unit tests for this
            Logger.LogInfo("Miner Action Handler", "Processing Miner Completed Actions");
            var totalAmountExtracted = calculationService.CalculateTotalAmountExtracted(resourceNode, playerActions);

            var calculatedTotalAmount =
                totalAmountExtracted < resourceNode.Amount ? totalAmountExtracted : resourceNode.Amount;

            var totalUnitsAtResource = playerActions.Sum(x => x.NumberOfUnits);

            foreach (var playerAction in playerActions)
            {
                var botPopulationTier = calculationService.GetBotPopulationTier(playerAction.Bot);

                double distributionFactor =
                    Convert.ToDouble(calculatedTotalAmount) / Convert.ToDouble(totalUnitsAtResource);
                var stoneDistributed = (int) Math.Round(playerAction.NumberOfUnits * distributionFactor);

                var maxResourceDistributed = botPopulationTier.TierMaxResources.Stone - playerAction.Bot.Stone;
                stoneDistributed = stoneDistributed.NeverMoreThan(maxResourceDistributed);
                
                Logger.LogInfo("Miner Action Handler",
                    $"Bot {playerAction.Bot.Id} received {stoneDistributed} amount of stone");
                playerAction.Bot.Stone += stoneDistributed;

                resourceNode.Amount -= stoneDistributed;
                resourceNode.CurrentUnits -= playerAction.NumberOfUnits;
            }
        }
    }
}