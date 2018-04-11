﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class Eth62ProtocolHandler : ProtocolHandlerBase, IProtocolHandler, ISynchronizationPeer
    {
        private readonly ISynchronizationManager _sync;

        public Eth62ProtocolHandler(
            IP2PSession p2PSession,
            IMessageSerializationService serializer,
            ISynchronizationManager sync,
            ILogger logger)
            : base(p2PSession, serializer, logger)
        {
            _sync = sync;
        }

        private bool _statusSent;

        private bool _statusReceived;

        public virtual byte ProtocolVersion => 62;

        public string ProtocolCode => "eth";

        public virtual int MessageIdSpaceSize => 7;

        public void Init()
        {
            Logger.Log($"{ProtocolCode} v{ProtocolVersion} subprotocol initializing");
            StatusMessage statusMessage = new StatusMessage();
            statusMessage.NetworkId =  _sync.ChainId;
            statusMessage.ProtocolVersion = ProtocolVersion;
            statusMessage.TotalDifficulty = _sync.TotalDifficulty;
            statusMessage.BestHash = _sync.BestBlock;
            statusMessage.GenesisHash = _sync.GenesisBlock;
            //

            _statusSent = true;
            Send(statusMessage);
        }

        private static readonly Dictionary<int, Type> MessageTypes = new Dictionary<int, Type>
        {
            {Eth62MessageCode.Status, typeof(StatusMessage)},
            {Eth62MessageCode.NewBlockHashes, typeof(NewBlockHashesMessage)},
            {Eth62MessageCode.Transactions, typeof(TransactionsMessage)},
            {Eth62MessageCode.GetBlockHeaders, typeof(GetBlockHeadersMessage)},
            {Eth62MessageCode.BlockHeaders, typeof(BlockHeadersMessage)},
            {Eth62MessageCode.GetBlockBodies, typeof(GetBlockBodiesMessage)},
            {Eth62MessageCode.BlockBodies, typeof(BlockBodiesMessage)},
            {Eth62MessageCode.NewBlock, typeof(NewBlockMessage)},
        };

        public virtual Type ResolveMessageType(int messageCode)
        {
            return MessageTypes[messageCode];
        }

        public virtual void HandleMessage(Packet message)
        {
            Logger.Log($"{nameof(Eth62ProtocolHandler)} handling a message with code {message.PacketType}.");

            if (message.PacketType != Eth62MessageCode.Status && !_statusReceived)
            {
                throw new SubprotocolException($"No {nameof(StatusMessage)} received prior to communication.");
            }

            switch (message.PacketType)
            {
                case Eth62MessageCode.Status:
                    Handle(Deserialize<StatusMessage>(message.Data));
                    break;
                case Eth62MessageCode.NewBlockHashes:
                    Handle(Deserialize<NewBlockHashesMessage>(message.Data));
                    break;
                case Eth62MessageCode.Transactions:
                    Handle(Deserialize<TransactionsMessage>(message.Data));
                    break;
                case Eth62MessageCode.GetBlockHeaders:
                    Handle(Deserialize<GetBlockHeadersMessage>(message.Data));
                    break;
                case Eth62MessageCode.BlockHeaders:
                    Handle(Deserialize<BlockHeadersMessage>(message.Data));
                    break;
                case Eth62MessageCode.GetBlockBodies:
                    Handle(Deserialize<GetBlockBodiesMessage>(message.Data));
                    break;
                case Eth62MessageCode.BlockBodies:
                    Handle(Deserialize<BlockBodiesMessage>(message.Data));
                    break;
                case Eth62MessageCode.NewBlock:
                    Handle(Deserialize<NewBlockMessage>(message.Data));
                    break;
            }
        }

        private GetBlockHeadersMessage _lastHeadersRequestSent;
        private GetBlockBodiesMessage _lastBodiesRequestSent;

        private void Handle(StatusMessage status)
        {
            if (_statusReceived)
            {
                throw new SubprotocolException($"{nameof(StatusMessage)} has already been received in the past");
            }

            _statusReceived = true;
            Logger.Log("ETH received status with" +
                       Environment.NewLine + $" prot version\t{status.ProtocolVersion}" +
                       Environment.NewLine + $" network ID\t{status.NetworkId}," +
                       Environment.NewLine + $" genesis hash\t{status.GenesisHash}," +
                       Environment.NewLine + $" best hash\t{status.BestHash}," +
                       Environment.NewLine + $" difficulty\t{status.TotalDifficulty}");

            _headBlockHash = status.BestHash;
            _headBlockDifficulty = status.TotalDifficulty;
            BlockInfo blockInfo = _sync.Find(status.BestHash);

            // need to ask about this
            // _headBlockNumber = blockInfo?.Number ?? ?;

            Debug.Assert(_statusSent, "Expecting Init() to have been called by this point");
            ProtocolInitialized?.Invoke(this, EventArgs.Empty);
        }

        private void Handle(TransactionsMessage transactionsMessage)
        {
            for (int txIndex = 0; txIndex < transactionsMessage.Transactions.Length; txIndex++)
            {
                Transaction transaction = transactionsMessage.Transactions[txIndex];
                TransactionInfo info = _sync.Add(transaction, P2PSession.RemoteNodeId);
                if (info.Quality == Quality.Invalid) // TODO: processed invalid should not be penalized here
                {
                    Logger.Debug($"Received an invalid transaction from {P2PSession.RemoteNodeId}");
                    throw new SubprotocolException($"Peer sent an invalid transaction {transaction.Hash}");
                }
            }
        }

        private void Handle(GetBlockBodiesMessage getBlockBodiesMessage)
        {
            throw new NotImplementedException();
        }

        private void Handle(GetBlockHeadersMessage getBlockHeadersMessage)
        {
            BlockInfo[] blockInfos = _sync.Find(getBlockHeadersMessage.StartingBlockHash, (int)getBlockHeadersMessage.MaxHeaders);
            BlockHeader[] blockHeaders = new BlockHeader[blockInfos.Length];
            for (int i = 0; i < blockInfos.Length; i++)
            {
                if (blockInfos[i] != null)
                {
                    if (blockInfos[i].HeaderLocation == BlockDataLocation.Store)
                    {
                        throw new NotImplementedException();
                    }

                    if (blockInfos[i].HeaderLocation == BlockDataLocation.Memory)
                    {
                        blockHeaders[i] = blockInfos[i].BlockHeader;
                    }
                    else
                    {
                        throw new NotImplementedException();
                    }
                }
            }

            Send(new BlockHeadersMessage(blockHeaders));
        }

        private TaskCompletionSource<Block[]> _blockHeadersTaskCompletion; // queues?
        private TaskCompletionSource<BigInteger> _numberCompletionSource; // work in progress

        private void Handle(BlockBodiesMessage blockBodies)
        {
            foreach ((Transaction[] Transactions, BlockHeader[] Ommers) body in blockBodies.Bodies)
            {
                // TODO: work in progress
            }
        }

        private void Handle(BlockHeadersMessage blockHeaders)
        {
            if (_blockHeadersTaskCompletion != null)
            {
                // TODO: rough, work in progress
                List<Block> blocks = new List<Block>();
                foreach (BlockHeader header in blockHeaders.BlockHeaders)
                {
//                BlockInfo blockInfo = _sync.AddBlockHeader(header);
//                if (blockInfo.HeaderQuality == Quality.Invalid)
//                {
//                    Logger.Debug($"Received an invalid header from {P2PSession.RemoteNodeId}");
//                    throw new SubprotocolException($"Peer sent an invalid header {header.Hash}");
//                }

                    blocks.Add(header == null ? null : new Block(header));
                }

                _blockHeadersTaskCompletion.SetResult(blocks.ToArray());
                _blockHeadersTaskCompletion = null;
            }
            else
            {
                Debug.Assert(_numberCompletionSource != null);
                // assume roughly it should not be null
                _numberCompletionSource.SetResult(blockHeaders.BlockHeaders[0].Number);
                _numberCompletionSource = null;
            }
        }

        private void Handle(NewBlockHashesMessage newBlockHashes)
        {
            foreach ((Keccak Hash, BigInteger Number) hint in newBlockHashes.BlockHashes)
            {
                _sync.HintBlock(hint.Hash, hint.Number);
            }
        }

        private void Handle(NewBlockMessage newBlock)
        {
            // TODO: use total difficulty here
            // TODO: can drop connection if processing of the block fails (not only validation?)
            BlockInfo blockInfo = _sync.AddBlock(newBlock.Block, P2PSession.RemoteNodeId);
            if (blockInfo.BlockQuality == Quality.Invalid)
            {
                throw new SubprotocolException($"Peer sent an invalid new block {newBlock.Block.Hash}");
            }
        }

        public void Close()
        {
        }

        public event EventHandler ProtocolInitialized;
        public event EventHandler<ProtocolEventArgs> SubprotocolRequested;

        private static class GenesisRopsten
        {
            public static BigInteger Difficulty { get; } = 0x100000; // 1,048,576
            public static Keccak GenesisHash { get; } = new Keccak(new Hex("0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d"));
            public static Keccak BestHash { get; } = new Keccak(new Hex("0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d"));
            public static int ChainId { get; } = 3;
        }

        private static class NewerRopsten // 2963492   
        {
            public static BigInteger Difficulty { get; } = 7984694325252517;
            public static Keccak GenesisHash { get; } = new Keccak(new Hex("0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d"));
            public static Keccak BestHash { get; } = new Keccak(new Hex("0x452a31d7627daa0a58e7bdcf4d3f9838e710b45220eb98b8c2cee5c71d5ed9aa"));
            public static int ChainId { get; } = 3;
        }

        private static class GenesisMainNet
        {
            public static BigInteger Difficulty { get; } = 17179869184;
            public static Keccak GenesisHash { get; } = new Keccak(new Hex("0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3"));
            public static Keccak BestHash { get; } = new Keccak(new Hex("0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3"));
            public static int ChainId { get; } = 1;
        }

        public async Task<Block[]> GetBlocks(Keccak blockHash, BigInteger maxBlocks)
        {
            _blockHeadersTaskCompletion = new TaskCompletionSource<Block[]>();
            return await _blockHeadersTaskCompletion.Task;
        }

        public Task<Keccak> GetHeadBlockHash()
        {
            return Task.FromResult(_headBlockHash);
        }

        public async Task<BigInteger> GetHeadBlockNumber()
        {
            var msg = new GetBlockHeadersMessage();
            msg.StartingBlockHash = _headBlockHash;
            msg.MaxHeaders = 1;
            msg.Reverse = 0;
            msg.Skip = 0;

            _numberCompletionSource = new TaskCompletionSource<BigInteger>();
            Send(msg);
            return await _numberCompletionSource.Task;
        }

        public event EventHandler<BlockEventArgs> NewBlock;
        public event EventHandler<KeccakEventArgs> NewBlockHash;

        private Keccak _headBlockHash;
        private BigInteger _headBlockNumber;
        private BigInteger _headBlockDifficulty;
    }
}