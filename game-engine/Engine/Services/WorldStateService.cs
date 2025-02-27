﻿using System;
using System.Collections.Generic;
using System.Linq;
using Domain.Enums;
using Domain.Models;
using Domain.Models.DTOs;
using Domain.Services;
using Engine.Interfaces;
using Engine.Models;
using Newtonsoft.Json;

namespace Engine.Services
{
    public class WorldStateService : IWorldStateService
    {
        private List<Position> presetBotLocations;
        private readonly EngineConfig engineConfig;
        private readonly ISet<Position> positionsInUse = new HashSet<Position>();
        private readonly ISet<Position> botPositionsInUse = new HashSet<Position>();
        private readonly ISet<Position> farmPositionsInUse = new HashSet<Position>();
        private readonly ISet<Position> woodPositionsInUse = new HashSet<Position>();
        private readonly ISet<Position> stonePositionsInUse = new HashSet<Position>();
        private readonly ISet<Position> scoutTowersPositionsInUse = new HashSet<Position>();

        private readonly GameState state = new()
        {
            World = null,
            Bots = new List<BotObject>()
        };

        private readonly GameStateDto publishedState = new()
        {
            World = null,
        };

        private Random randomGenerator;

        public WorldStateService(
            IConfigurationService engineConfigOptions)
        {
            engineConfig = engineConfigOptions.Value;
            state.World = new World
            {
                Size = engineConfig.WorldLength,
                CurrentTick = 0
            };
            InitPresetBotLocations();

            var worldSeed = engineConfig.WorldSeed;
            randomGenerator = new Random(worldSeed);
        }

        private void InitPresetBotLocations()
        {
            var positionFactor = engineConfig.NumberOfRegionsInMapLength / 4 * engineConfig.RegionSize;
            presetBotLocations = new List<Position>
            {
                new(1 * positionFactor, 1 * positionFactor),
                new(1 * positionFactor, 3 * positionFactor),
                new(3 * positionFactor, 1 * positionFactor),
                new(3 * positionFactor, 3 * positionFactor)
            };

            positionsInUse.Add(presetBotLocations[0]);
            botPositionsInUse.Add(presetBotLocations[0]);
            positionsInUse.Add(presetBotLocations[1]);
            botPositionsInUse.Add(presetBotLocations[1]);
            positionsInUse.Add(presetBotLocations[2]);
            botPositionsInUse.Add(presetBotLocations[2]);
            positionsInUse.Add(presetBotLocations[3]);
            botPositionsInUse.Add(presetBotLocations[3]);
        }

        public BotObject GetBotById(Guid botId)
        {
            return state.Bots.Find(c => c.Id == botId);
        }

        public void AddBotObject(BotObject bot)
        {
            state.Bots.Add(bot);
        }

        public int GetPlayerCount()
        {
            return state.Bots.Count;
        }

        public GameStateDto GetPublishedState()
        {
            var stoplog = new StopWatchLogger();
            publishedState.World = state.World;
            publishedState.Bots = state.Bots.Select(bot => bot.ToStateObject(state)).ToList();
            stoplog.Log("Got Published state");

            return publishedState;
        }

        public GameState GetState()
        {
            return state;
        }

        public BotObject CreateBotObject(Guid id)
        {
            var bot = new BotObject
            {
                Id = id,
                CurrentTierLevel = 0,
                Map = new BotMapState(),
                Food = engineConfig.StartingFood,
                AvailableUnits = engineConfig.StartingUnits,
                Population = engineConfig.StartingUnits,
                Position = GetNextBotPosition(),
                Seed = engineConfig.Seeds.PlayerSeeds[state.Bots.Count]
            };
            state.Bots.Add(bot);
            Logger.LogDebug("WorldGen", $"Adding Bot {id} at position x: {bot.Position.X}, y: {bot.Position.Y}");
            return bot;
        }

        private Position GetNextBotPosition()
        {
            var randomIndex = randomGenerator.Next(0, presetBotLocations.Count);
            var position = presetBotLocations[randomIndex];
            presetBotLocations.RemoveAt(randomIndex);
            return position;
        }

        public IList<BotObject> GetPlayerBots()
        {
            return state.Bots.ToList();
        }

        public ResourceNode GetResourceNode(Guid id)
        {
            return state.World.Map.ResolveNode(id);
        }

        public void AddResourceToMap(ResourceNode resourceNode)
        {
            state.World.Map.Nodes.Add(resourceNode);
        }

        public ScoutTower GetScoutTower(Guid id)
        {
            return state.World.GetScoutTower(id);
        }

        public ResourceNode GetScoutTowerAsResourceNode(Guid id)
        {
            var scoutTower = GetScoutTower(id);
            return scoutTower != null
                ? new ResourceNode
                {
                    Position = scoutTower.Position,
                    WorkTime = engineConfig.ScoutWorkTime,
                }
                : null;
        }

        public ResourceNode GetBaseAsResourceNode(BotObject bot)
        {
            return new ResourceNode
            {
                Position = bot.Position,
                WorkTime = randomGenerator.Next(engineConfig.ResourceGenerationConfig.Campfire.WorkTimeRange[0],
                    engineConfig.ResourceGenerationConfig.Campfire.WorkTimeRange[1])
            };
        }

        public ResourceNode ResolveNode(PlayerAction playerAction)
        {
            switch (playerAction.ActionType)
            {
                case ActionType.Scout:
                    return GetScoutTowerAsResourceNode(playerAction.TargetNodeId);
                case ActionType.StartCampfire:
                    return GetBaseAsResourceNode(playerAction.Bot);
                case ActionType.Mine:
                case ActionType.Farm:
                case ActionType.Lumber:
                    return GetResourceNode(playerAction.TargetNodeId);
                case ActionType.Error:
                default:
                    return null;
            }
        }


        public void GenerateStartingWorld()
        {
            Logger.LogDebug("WorldGen", "Generating Starting World");
            Logger.LogDebug("WorldGen", "Using the following config values");
            Logger.LogData(JsonConvert.SerializeObject(engineConfig, Formatting.Indented));
            List<int> playerSeeds = engineConfig.Seeds.PlayerSeeds;
            Logger.LogDebug("WorldGen", $"Player Seeds: [{string.Join(" ", playerSeeds)}]");

            GenerateMap();
        }

        public GameCompletePayload GenerateGameCompletePayload()
        {
            try
            {
                return new GameCompletePayload
                {
                    TotalTicks = state.World.CurrentTick,
                    WinningBot = GetPlayerBots().OrderByDescending(bot => bot.Score).First()
                        .ToStateObject(state),
                    WorldSeeds = new List<int>
                    {
                        engineConfig.WorldSeed
                    },
                    Players = GeneratePlayerResults()
                };
            }
            catch (InvalidOperationException)
            {
                Logger.LogError("GameComplete", "One or more seeds were null at the end of game");
                throw;
            }
        }

        private Position GenerateRandomPosition(int lowerX, int upperX, int lowerY, int upperY,
            ISet<Position> proximityPositions, int minDistance,
            bool baseZone, bool nonBaseZone)
        {
            var count = 0;
            Position position;

            // Maximum attempts
            while (count < engineConfig.RegionSize * engineConfig.RegionSize)
            {
                position = new Position
                {
                    X = randomGenerator.Next(lowerX, upperX), Y = randomGenerator.Next(lowerY, upperY)
                };
                count++;

                if (positionsInUse.All(pos => pos != position))
                {
                    if (minDistance > 0 && proximityPositions.Any(pos => InRadiusRange(position, pos, minDistance)))
                    {
                        continue;
                    }

                    if (baseZone && !nonBaseZone && !IsBaseZone(position))
                    {
                        continue;
                    }

                    if (nonBaseZone && !baseZone && IsBaseZone(position))
                    {
                        continue;
                    }

                    // Add to positions in use
                    positionsInUse.Add(position);
                    proximityPositions.Add(position);

                    return position;
                }
            }

            return null;
        }

        private ScoutTower CreateScoutTower(int i, int j)
        {
            var regionSize = engineConfig.RegionSize;

            int lowerX = i * regionSize;
            int upperX = (i + 1) * regionSize;
            int lowerY = j * regionSize;
            int upperY = (j + 1) * regionSize;

            var newScoutTower = new ScoutTower
            {
                Id = Guid.NewGuid(),
                Position = GenerateRandomPosition(lowerX, upperX, lowerY, upperY, scoutTowersPositionsInUse, 1,
                    false, true)
            };
            positionsInUse.Add(newScoutTower.Position);
            return newScoutTower;
        }

        public void Initialize()
        {
            Logger.LogInfo("Core", "Initialize World State");
            GenerateStartingWorld();
            var visualizer = new AsciiVisualizerService(engineConfig.WorldLength);

            // Console.Write(visualizer.GenerateMap(state));
        }

        private Position GenerateValidPosition(int regionX, int regionY, ResourceConfig config,
            ISet<Position> proximityPositions)
        {
            var startingX = regionX * engineConfig.RegionSize;
            var startingY = regionY * engineConfig.RegionSize;
            return GenerateRandomPosition(startingX, startingX + engineConfig.RegionSize,
                startingY,
                startingY + engineConfig.RegionSize,
                proximityPositions,
                config.ProximityDistance,
                config.DistributionZones.Contains("Base"),
                config.DistributionZones.Contains("NonBase")
            );
        }

        private bool IsBaseZone(Position position)
        {
            return botPositionsInUse.Any(x => InRadiusRange(position, x, engineConfig.BaseZoneSize));
        }

        private bool InRadiusRange(Position position, Position target, int distance)
        {
            var calculatedDistance = Math.Sqrt(Math.Pow(target.X - position.X, 2) + Math.Pow(target.Y - position.Y, 2));
            return calculatedDistance <= distance;
        }

        public void GenerateMap()
        {
            var scoutTowers = new List<ScoutTower>();
            var nodes = new List<ResourceNode>();

            var regionIndices = Enumerable.Range(0, engineConfig.NumberOfRegionsInMapLength);
            var shuffledRegionsIndices = regionIndices.OrderBy(item => randomGenerator.Next()).ToList();

            foreach (var x in shuffledRegionsIndices)
            {
                foreach (var y in shuffledRegionsIndices)
                {
                    // Calculate amount of certain resource in region
                    // Generate valid positions for each node based on distribution - if no valid position do not place node

                    // Create region scout tower
                    var regionScoutTower = CreateScoutTower(x, y);
                    scoutTowers.Add(regionScoutTower);

                    // Farms
                    // TODO: refactor to return a list of farms that get added to the regionScoutTower
                    var numberOfFarms = randomGenerator.Next(
                        engineConfig.ResourceGenerationConfig.Farm.QuantityRangePerRegion[0],
                        engineConfig.ResourceGenerationConfig.Farm.QuantityRangePerRegion[1]);
                    for (int i = 0; i < numberOfFarms; i++)
                    {
                        var validPosition = GenerateValidPosition(x, y, engineConfig.ResourceGenerationConfig.Farm,
                            farmPositionsInUse);
                        if (validPosition is null)
                        {
                            continue;
                        }

                        var amount = randomGenerator.Next(
                            engineConfig.ResourceGenerationConfig.Farm.AmountRange[0],
                            engineConfig.ResourceGenerationConfig.Farm.AmountRange[1]);
                        var node = new ResourceNode()
                        {
                            Id = Guid.NewGuid(),
                            Position = validPosition,
                            Type = ResourceType.Food,
                            Amount = amount,
                            MaxResourceAmount = amount,
                            MaxUnits = randomGenerator.Next(
                                engineConfig.ResourceGenerationConfig.Farm.MaxUnitsRange[0],
                                engineConfig.ResourceGenerationConfig.Farm.MaxUnitsRange[1]),
                            Reward = randomGenerator.Next(
                                engineConfig.ResourceGenerationConfig.Farm.RewardRange[0],
                                engineConfig.ResourceGenerationConfig.Farm.RewardRange[1]),
                            WorkTime = randomGenerator.Next(
                                engineConfig.ResourceGenerationConfig.Farm.WorkTimeRange[0],
                                engineConfig.ResourceGenerationConfig.Farm.WorkTimeRange[1]),
                            RegenerationRate = new RegenerationRate()
                            {
                                Amount = randomGenerator.Next(
                                    engineConfig.ResourceGenerationConfig.Farm.RegenerationRateRange.AmountRange[0],
                                    engineConfig.ResourceGenerationConfig.Farm.RegenerationRateRange
                                        .AmountRange[1]),
                                Ticks = randomGenerator.Next(
                                    engineConfig.ResourceGenerationConfig.Farm.RegenerationRateRange.TickRange[0],
                                    engineConfig.ResourceGenerationConfig.Farm.RegenerationRateRange.TickRange[1])
                            },
                            CurrentRegenTick = 0
                        };
                        nodes.Add(node);
                        regionScoutTower.Nodes.Add(node.Id);
                    }

                    // Wood
                    var numberOfWoodNodes = randomGenerator.Next(
                        engineConfig.ResourceGenerationConfig.Wood.QuantityRangePerRegion[0],
                        engineConfig.ResourceGenerationConfig.Wood.QuantityRangePerRegion[1]);
                    for (int i = 0; i < numberOfWoodNodes; i++)
                    {
                        var validPosition = GenerateValidPosition(x, y, engineConfig.ResourceGenerationConfig.Wood,
                            woodPositionsInUse);
                        if (validPosition is null)
                        {
                            continue;
                        }

                        var node = new ResourceNode()
                        {
                            Id = Guid.NewGuid(),
                            Position = validPosition,
                            Type = ResourceType.Wood,
                            MaxUnits = randomGenerator.Next(
                                engineConfig.ResourceGenerationConfig.Wood.MaxUnitsRange[0],
                                engineConfig.ResourceGenerationConfig.Wood.MaxUnitsRange[1]),
                            Reward = randomGenerator.Next(
                                engineConfig.ResourceGenerationConfig.Wood.RewardRange[0],
                                engineConfig.ResourceGenerationConfig.Wood.RewardRange[1]),
                            WorkTime = randomGenerator.Next(
                                engineConfig.ResourceGenerationConfig.Wood.WorkTimeRange[0],
                                engineConfig.ResourceGenerationConfig.Wood.WorkTimeRange[1]),
                            Amount = randomGenerator.Next(
                                engineConfig.ResourceGenerationConfig.Wood.AmountRange[0],
                                engineConfig.ResourceGenerationConfig.Wood.AmountRange[1])
                        };
                        nodes.Add(node);
                        regionScoutTower.Nodes.Add(node.Id);
                    }

                    // Stone
                    var numberOfStoneNodes = randomGenerator.Next(
                        engineConfig.ResourceGenerationConfig.Stone.QuantityRangePerRegion[0],
                        engineConfig.ResourceGenerationConfig.Stone.QuantityRangePerRegion[1]);
                    for (int i = 0; i < numberOfStoneNodes; i++)
                    {
                        var validPosition = GenerateValidPosition(x, y, engineConfig.ResourceGenerationConfig.Stone,
                            stonePositionsInUse);
                        if (validPosition is null)
                        {
                            continue;
                        }

                        var node = new ResourceNode()
                        {
                            Id = Guid.NewGuid(),
                            Position = validPosition,
                            Type = ResourceType.Stone,
                            MaxUnits = randomGenerator.Next(
                                engineConfig.ResourceGenerationConfig.Stone.MaxUnitsRange[0],
                                engineConfig.ResourceGenerationConfig.Stone.MaxUnitsRange[1]),
                            Reward = randomGenerator.Next(
                                engineConfig.ResourceGenerationConfig.Stone.RewardRange[0],
                                engineConfig.ResourceGenerationConfig.Stone.RewardRange[1]),
                            WorkTime = randomGenerator.Next(
                                engineConfig.ResourceGenerationConfig.Stone.WorkTimeRange[0],
                                engineConfig.ResourceGenerationConfig.Stone.WorkTimeRange[1]),
                            Amount = randomGenerator.Next(
                                engineConfig.ResourceGenerationConfig.Stone.AmountRange[0],
                                engineConfig.ResourceGenerationConfig.Stone.AmountRange[1])
                        };
                        nodes.Add(node);
                        regionScoutTower.Nodes.Add(node.Id);
                    }
                }
            }

            state.World = World.Create(nodes, scoutTowers, engineConfig.WorldLength);
        }

        public int GetCurrentTick()
        {
            return state.World.CurrentTick;
        }

        public IEnumerable<Guid> GetScoutTowerInformation(Guid id)
        {
            var scoutTower = state.World.GetScoutTower(id);
            return scoutTower != null ? scoutTower.Nodes : new List<Guid>();
        }

        public string GetWorldState()
        {
            return state.World.Map.ToString();
        }

        public void ApplyAfterTickStateChanges()
        {
            ModifyWorldBaseStateForNextTick();
        }

        private void ModifyWorldBaseStateForNextTick()
        {
            state.World.CurrentTick++;
        }

        private List<PlayerResult> GeneratePlayerResults() =>
            state.Bots.Select(
                    bot => new PlayerResult
                    {
                        Id = bot.Id.ToString(),
                        Placement = bot.Placement,
                        MatchPoints = GetMatchPointsFromPlacement(bot.Placement),
                        Score = bot.Score,
                        Nickname = string.Empty,
                        Seed = bot.Seed != default
                            ? bot.Seed
                            : 0 //throw new InvalidOperationException("Null Player Seed")
                    })
                .ToList();

        private int GetMatchPointsFromPlacement(int placement) => (engineConfig.BotCount - placement + 1) * 2;
    }
}