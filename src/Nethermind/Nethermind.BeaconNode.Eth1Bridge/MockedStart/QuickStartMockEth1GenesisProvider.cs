﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Eth1;
using Nethermind.Core2.Types;
using Nethermind.Cryptography;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging.Microsoft;
using Nethermind.Merkleization;
using Nethermind.Ssz;

namespace Nethermind.BeaconNode.Eth1Bridge.MockedStart
{
    public class QuickStartMockEth1GenesisProvider : IEth1GenesisProvider
    {
        private readonly IBeaconChainUtility _beaconChainUtility;
        private readonly ChainConstants _chainConstants;
        private readonly ICryptographyService _cryptographyService;

        private readonly IOptionsMonitor<GweiValues> _gweiValueOptions;
        private readonly IOptionsMonitor<InitialValues> _initialValueOptions;
        private readonly ILogger<QuickStartMockEth1GenesisProvider> _logger;
        private readonly IOptionsMonitor<QuickStartParameters> _quickStartParameterOptions;
        private readonly IOptionsMonitor<SignatureDomains> _signatureDomainOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        private static readonly BigInteger s_curveOrder =
            BigInteger.Parse("52435875175126190479447740508185965837690552500527637822603658699938581184513");

        public QuickStartMockEth1GenesisProvider(ILogger<QuickStartMockEth1GenesisProvider> logger,
            ChainConstants chainConstants,
            IOptionsMonitor<GweiValues> gweiValueOptions,
            IOptionsMonitor<InitialValues> initialValueOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<SignatureDomains> signatureDomainOptions,
            IOptionsMonitor<QuickStartParameters> quickStartParameterOptions,
            ICryptographyService cryptographyService,
            IBeaconChainUtility beaconChainUtility)
        {
            _logger = logger;
            _chainConstants = chainConstants;
            _gweiValueOptions = gweiValueOptions;
            _initialValueOptions = initialValueOptions;
            _timeParameterOptions = timeParameterOptions;
            _signatureDomainOptions = signatureDomainOptions;
            _quickStartParameterOptions = quickStartParameterOptions;
            _cryptographyService = cryptographyService;
            _beaconChainUtility = beaconChainUtility;
        }

        public byte[] GeneratePrivateKey(ulong index)
        {
            Span<byte> input = new Span<byte>(new byte[32]);
            BigInteger bigIndex = new BigInteger(index);
            bool indexWriteSuccess =
                bigIndex.TryWriteBytes(input, out int indexBytesWritten, isUnsigned: true, isBigEndian: false);
            if (!indexWriteSuccess || indexBytesWritten == 0)
            {
                throw new Exception("Error getting input for quick start private key generation.");
            }

            Bytes32 hash32 = _cryptographyService.Hash(input);
            ReadOnlySpan<byte> hash = hash32.AsSpan();
            // Mocked start interop specifies to convert the hash as little endian (which is the default for BigInteger)
            BigInteger value = new BigInteger(hash.ToArray(), isUnsigned: true);
            BigInteger privateKey = value % s_curveOrder;

            // Note that the private key is an *unsigned*, *big endian* number
            // However, we want to pad the big endian on the left to get 32 bytes.
            // So, write as little endian (will pad to right), then reverse.
            // NOTE: Alternative, write to Span 64, and then slice based on bytesWritten to get the padding.
            Span<byte> privateKeySpan = new Span<byte>(new byte[32]);
            bool keyWriteSuccess = privateKey.TryWriteBytes(privateKeySpan, out int keyBytesWritten, isUnsigned: true,
                isBigEndian: false);
            if (!keyWriteSuccess)
            {
                throw new Exception("Error generating quick start private key.");
            }

            privateKeySpan.Reverse();

            return privateKeySpan.ToArray();
        }
        
        public Task<Eth1GenesisData> GetEth1GenesisDataAsync(CancellationToken cancellationToken)
        {
            QuickStartParameters quickStartParameters = _quickStartParameterOptions.CurrentValue;

            if (_logger.IsWarn())
                Log.MockedQuickStart(_logger, quickStartParameters.GenesisTime, quickStartParameters.ValidatorCount,
                    null);

            GweiValues gweiValues = _gweiValueOptions.CurrentValue;
            InitialValues initialValues = _initialValueOptions.CurrentValue;
            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            SignatureDomains signatureDomains = _signatureDomainOptions.CurrentValue;

            // Fixed amount
            Gwei amount = gweiValues.MaximumEffectiveBalance;
            
            List<Deposit> deposits = new List<Deposit>();
            for (ulong validatorIndex = 0uL; validatorIndex < quickStartParameters.ValidatorCount; validatorIndex++)
            {
                if (validatorIndex > 1000)
                {
                    
                }
                byte[] privateKey = GeneratePrivateKey(validatorIndex);

                // Public Key
                BLSParameters blsParameters = new BLSParameters()
                {
                    PrivateKey = privateKey
                };
                using BLS bls = BLS.Create(blsParameters);
                byte[] publicKeyBytes = new byte[BlsPublicKey.Length];
                bls.TryExportBlsPublicKey(publicKeyBytes, out int publicKeyBytesWritten);
                BlsPublicKey publicKey = new BlsPublicKey(publicKeyBytes);

                // Withdrawal Credentials
                byte[] withdrawalCredentialBytes = _cryptographyService.Hash(publicKey.AsSpan()).AsSpan().ToArray();
                withdrawalCredentialBytes[0] = initialValues.BlsWithdrawalPrefix;
                Bytes32 withdrawalCredentials = new Bytes32(withdrawalCredentialBytes);

                // Build deposit data
                DepositData depositData = new DepositData(publicKey, withdrawalCredentials, amount, BlsSignature.Zero);

                // Sign deposit data
                Domain domain = _beaconChainUtility.ComputeDomain(signatureDomains.Deposit);
                DepositMessage depositMessage = new DepositMessage(depositData.PublicKey,
                    depositData.WithdrawalCredentials, depositData.Amount);
                Root depositMessageRoot = _cryptographyService.HashTreeRoot(depositMessage);
                Root depositDataSigningRoot = _beaconChainUtility.ComputeSigningRoot(depositMessageRoot, domain);
                byte[] destination = new byte[96];
                bls.TrySignData(depositDataSigningRoot.AsSpan(), destination, out int bytesWritten);
                BlsSignature depositDataSignature = new BlsSignature(destination);
                depositData.SetSignature(depositDataSignature);

                int index = deposits.Count;
                Ref<DepositData> depositDataRef = depositData.OrRoot;

                Merkle.Ize(out UInt256 root2Num, depositDataRef);
                Root leaf = new Root(root2Num);
                _shaMerkleTree.Insert(Bytes32.Wrap(leaf.Bytes));
                var proof2 = _shaMerkleTree.GetProof((uint)index);

                byte[] indexBytes = new byte[32];
                BinaryPrimitives.WriteInt32LittleEndian(indexBytes, index + 1);
                Bytes32 indexHash = new Bytes32(indexBytes);
                proof2.Add(indexHash);
                bool isValid = _beaconChainUtility.IsValidMerkleBranch(
                    Bytes32.Wrap(leaf.Bytes),
                    proof2,
                    _chainConstants.DepositContractTreeDepth + 1,
                    (ulong) index,
                    Root.Wrap(_shaMerkleTree.Root.Unwrap()));
                
                if (!isValid)
                {
                    throw new InvalidDataException("Invalid deposit");
                }
                
                Deposit deposit = new Deposit(proof2, depositDataRef);

                if (_logger.IsEnabled(LogLevel.Debug))
                    LogDebug.QuickStartAddValidator(_logger, validatorIndex, publicKey.ToString().Substring(0, 12),
                        null);

                deposits.Add(deposit);
            }

            ulong eth1Timestamp = quickStartParameters.Eth1Timestamp;
            if (eth1Timestamp == 0)
            {
                eth1Timestamp = quickStartParameters.GenesisTime - (ulong) (1.5 * timeParameters.MinimumGenesisDelay);
            }
            else
            {
                ulong minimumEth1TimestampInclusive =
                    quickStartParameters.GenesisTime - 2 * timeParameters.MinimumGenesisDelay;
                ulong maximumEth1TimestampInclusive =
                    quickStartParameters.GenesisTime - timeParameters.MinimumGenesisDelay - 1;
                if (eth1Timestamp < minimumEth1TimestampInclusive)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        Log.QuickStartEth1TimestampTooLow(_logger, eth1Timestamp, quickStartParameters.GenesisTime,
                            minimumEth1TimestampInclusive, null);
                    eth1Timestamp = minimumEth1TimestampInclusive;
                }
                else if (eth1Timestamp > maximumEth1TimestampInclusive)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        Log.QuickStartEth1TimestampTooHigh(_logger, eth1Timestamp, quickStartParameters.GenesisTime,
                            maximumEth1TimestampInclusive, null);
                    eth1Timestamp = maximumEth1TimestampInclusive;
                }
            }

            var eth1GenesisData = new Eth1GenesisData(quickStartParameters.Eth1BlockHash, eth1Timestamp, deposits);

            if (_logger.IsEnabled(LogLevel.Debug))
                LogDebug.QuickStartGenesisDataCreated(_logger, eth1GenesisData.BlockHash, eth1GenesisData.Timestamp,
                    eth1GenesisData.Deposits.Count, null);

            return Task.FromResult(eth1GenesisData);
        }
        
        private ShaMerkleTree _shaMerkleTree = new ShaMerkleTree(new MemMerkleTreeStore());
    }
}