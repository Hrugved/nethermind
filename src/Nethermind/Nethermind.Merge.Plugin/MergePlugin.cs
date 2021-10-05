﻿//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin
{
    public partial class MergePlugin : IConsensusWrapperPlugin
    {
        private INethermindApi _api = null!;
        private ILogger _logger = null!;
        private IMergeConfig _mergeConfig = null!;
        private IPoSSwitcher _poSSwitcher = null!;
        private ITransitionProcessHandler _transitionProcessHandler => (ITransitionProcessHandler)_poSSwitcher;
        private ManualBlockFinalizationManager _blockFinalizationManager = null!;

        public string Name => "Merge";
        public string Description => "Merge plugin for ETH1-ETH2";
        public string Author => "Nethermind";

        public Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;
            _mergeConfig = nethermindApi.Config<IMergeConfig>();
            _logger = _api.LogManager.GetClassLogger();

            // if (_mergeConfig.Enabled)
            {
                if (_api.DbProvider == null) throw new ArgumentException(nameof(_api.DbProvider));
                // if (string.IsNullOrEmpty(_mergeConfig.BlockAuthorAccount))
                // {
                //     if (_logger.IsError)
                //         _logger.Error(
                //             $"{nameof(MergeConfig)}.{nameof(_mergeConfig.BlockAuthorAccount)} is not set up. Cannot create blocks. Stopping.");
                //     // TODO: where the 13 coming from?
                //     Environment.Exit(13); // ERROR_INVALID_DATA
                // }

                _poSSwitcher = new PoSSwitcher(_api.LogManager, _mergeConfig, _api.DbProvider.GetDb<IDb>(DbNames.Metadata));
                _api.EngineSigner = new Eth2Signer(new Address(_mergeConfig.BlockAuthorAccount ?? Address.Zero.ToString()));
                _api.RewardCalculatorSource =
                    new MergeRewardCalculatorSource(_poSSwitcher,
                        _api.RewardCalculatorSource ?? NoBlockRewards.Instance);
                _api.SealEngine = new MergeSealEngine(_api.SealEngine, _poSSwitcher, _api.EngineSigner);
                _api.GossipPolicy = new MergeGossipPolicy(_api.GossipPolicy, _poSSwitcher);
            }
            
            _poSSwitcher = NoPoS.Instance;

            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            _api.HealthHintService =
                new MergeHealthHintService(_api.HealthHintService, _poSSwitcher);
            
            if (_mergeConfig.Enabled)
            {
                ISyncConfig syncConfig = _api.Config<ISyncConfig>();
                syncConfig.SynchronizationEnabled = false;
                syncConfig.BlockGossipEnabled = false;
                _blockFinalizationManager = new ManualBlockFinalizationManager();
                _api.FinalizationManager = _blockFinalizationManager;
            }
            
            return Task.CompletedTask;
        }
        
        public async Task InitRpcModules()
        {
            if (_mergeConfig.Enabled)
            {
                if (_api.RpcModuleProvider is null) throw new ArgumentNullException(nameof(_api.RpcModuleProvider));
                if (_api.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
                if (_api.BlockchainProcessor is null) throw new ArgumentNullException(nameof(_api.BlockchainProcessor));
                if (_api.StateProvider is null) throw new ArgumentNullException(nameof(_api.StateProvider));
                if (_api.StateProvider is null) throw new ArgumentNullException(nameof(_api.StateProvider));

                IInitConfig? initConfig = _api.Config<IInitConfig>();
                _api.Config<IJsonRpcConfig>().EnableModules(ModuleType.Engine);

                PayloadStorage payloadStorage = new(_defaultBlockProductionTrigger, _emptyBlockProductionTrigger, _api.StateProvider, _api.BlockchainProcessor, initConfig);
                PayloadManager payloadManager = new(_api.BlockTree);

                IEngineRpcModule engineRpcModule = new EngineRpcModule(
                    new PreparePayloadHandler(_api.BlockTree, payloadStorage, _defaultBlockProductionTrigger,
                        _emptyBlockProductionTrigger, _manualTimestamper, _api.Sealer, _api.LogManager),
                    new GetPayloadHandler(payloadStorage, _api.LogManager),
                    new ExecutePayloadHandler(_api.BlockTree, _api.BlockPreprocessor, _api.BlockchainProcessor,
                        payloadManager, _api.EthSyncingInfo, _api.StateProvider, _api.Config<IInitConfig>(),
                        _api.LogManager),
                    new ConsensusValidatedHandler(payloadManager),
                    _transitionProcessHandler,
                    new ForkChoiceUpdatedHandler(_api.BlockTree, _api.StateProvider, _blockFinalizationManager,
                        _poSSwitcher, _api.BlockConfirmationManager, _api.LogManager),
                    new ExecutionStatusHandler(_api.BlockTree, _api.BlockConfirmationManager,
                        _blockFinalizationManager),
                    _api.LogManager);

                _api.RpcModuleProvider.RegisterSingle(engineRpcModule);
                if (_logger.IsInfo) _logger.Info("Consensus Module has been enabled");
            }
        }

        public void AfterHeaderValidator()
        {
            _api.HeaderValidator = new MergeHeaderValidator(
                _api.HeaderValidator,
                _api.BlockTree,
                _api.SpecProvider,
                _poSSwitcher,
                _api.LogManager);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public string SealEngineType => "Eth2Merge";
    }
}
