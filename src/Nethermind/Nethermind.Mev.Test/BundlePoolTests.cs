//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avro.File;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using Nethermind.Runner.Ethereum.Api;
using NSubstitute;
using NSubstitute.Exceptions;
using NUnit.Framework;

namespace Nethermind.Mev.Test
{
    [TestFixture]
    public class BundlePoolTests
    {
        private const ulong DefaultTimestamp = 1_000_000;
        
        public static IEnumerable BundleRetrievalTest
        {
            get
            {
                yield return new BundleTest(8, DefaultTimestamp, 0, 4);
                yield return new BundleTest(9, DefaultTimestamp, 1, 5);
                yield return new BundleTest(10, 8, 0, 2, 
                    p => p.AddBundle(new MevBundle(10, Array.Empty<Transaction>(), 5, 7)));
                yield return new BundleTest(11, DefaultTimestamp, 0, 2);
                yield return new BundleTest(12, DefaultTimestamp, 1, 2);
                yield return new BundleTest(13, DefaultTimestamp, 0, 1);
                yield return new BundleTest(14, DefaultTimestamp, 0, 1);
                yield return new BundleTest(15, DefaultTimestamp, 1, 1);
                yield return new BundleTest(16, DefaultTimestamp, 0, 0);
            }
        }
        
        [TestCaseSource(nameof(BundleRetrievalTest))]
        public void should_retrieve_right_bundles_from_pool(BundleTest test)
        {
            BundlePool bundlePool = CreateBundlePool();
            if(test.Action != null) test.Action(bundlePool);
            List<MevBundle> result = bundlePool.GetBundles(test.Block, test.TestTimestamp).ToList();
            result.Count.Should().Be(test.ExpectedCount);
        }
        
        [TestCaseSource(nameof(BundleRetrievalTest))]
        public void should_retire_bundles_from_pool_after_finalization(BundleTest test)
        {
            IBlockFinalizationManager blockFinalizationManager = Substitute.For<IBlockFinalizationManager>();
            BundlePool bundlePool = CreateBundlePool();
            FinalizeEventArgs finalizeEventArgs = new(
                Build.A.BlockHeader.WithNumber(test.Block).TestObject, 
                Build.A.BlockHeader.WithNumber(test.Block-1).TestObject
            );
            
            blockFinalizationManager.BlocksFinalized += Raise.EventWith(finalizeEventArgs);
            if(test.Action != null) test.Action(bundlePool);
            List<MevBundle> result = bundlePool.GetBundles(test.Block, test.TestTimestamp).ToList();
            result.Count.Should().Be(test.ExpectedCount);
        }

        private static BundlePool CreateBundlePool()
        {
            BundlePool bundlePool = new(
                Substitute.For<IBlockTree>(),
                Substitute.For<IBundleSimulator>(),
                new Timestamper(),
                new MevConfig(),
                LimboLogs.Instance);

            bundlePool.AddBundle(new MevBundle(4, Array.Empty<Transaction>(), 0, 0));
            bundlePool.AddBundle(new MevBundle(5, Array.Empty<Transaction>(), 0, 0));
            bundlePool.AddBundle(new MevBundle(6, Array.Empty<Transaction>(), 0, 0));
            bundlePool.AddBundle(new MevBundle(9, Array.Empty<Transaction>(), 0, 0));
            bundlePool.AddBundle(new MevBundle(9, Array.Empty<Transaction>(), 0, long.MaxValue));
            bundlePool.AddBundle(new MevBundle(9, Array.Empty<Transaction>(), 0, DefaultTimestamp - 1));
            bundlePool.AddBundle(new MevBundle(12, Array.Empty<Transaction>(), 0, 0));
            bundlePool.AddBundle(new MevBundle(15, Array.Empty<Transaction>(), 0, 0));
            
            return bundlePool;
        }
        
        
        [Test]
        public static void should_add_bundle_with_correct_timestamps()
        {
            ITimestamper timestamper = new ManualTimestamper(new DateTime(2021, 1, 1));
            ulong timestamp = timestamper.UnixTime.Seconds;
            
            BundlePool bundlePool = new(
                Substitute.For<IBlockTree>(),
                Substitute.For<IBundleSimulator>(),
                timestamper,
                new MevConfig(),
                LimboLogs.Instance);

            Transaction[] txs = Array.Empty<Transaction>();
            MevBundle[] bundles = new []
            {
                new MevBundle(1, txs, 0, 0), //should get added
                new MevBundle(2, txs, 5, 0), //should not get added, min > max
                new MevBundle(3,  txs, timestamp + 50, timestamp + 100), //should get added
                new MevBundle(4,  txs, timestamp + 4000, timestamp + 5000), //should not get added, min time too large
                
            };

            bundles.Select(b => bundlePool.AddBundle(b))
                .Should().BeEquivalentTo(new bool[] {true, false, true, false});
        }

        [Test]
        public static void sort_bundles_by_increasing_block_number_and_then_min_timestamp()
        {
            ITimestamper timestamper = new ManualTimestamper(new DateTime(2021, 1, 1)); 
            ulong timestamp = timestamper.UnixTime.Seconds;
            
            Transaction[] txs = Array.Empty<Transaction>();
            IBlockTree blockTreeSub = Substitute.For<IBlockTree>();
            
            BlockHeader blockHeader = new BlockHeader(Keccak.EmptyTreeHash, Keccak.EmptyTreeHash, new Address(Keccak.EmptyTreeHash), 1, 3, 0,
                timestamp, new byte[3]); //creating BlockHeader with number 3
            blockTreeSub.BestSuggestedHeader.Returns(blockHeader); //setting BestSugggestedHeader.Number to blockHeader number
            Block block = new Block(blockHeader);
            blockTreeSub.Head.Returns(block); //setting Head.Number to blockHeader number
            
            MevConfig mevConfig = new MevConfig();
            int capacity = 5;
            mevConfig.BundlePoolSize = capacity; //creating capacity of 10
            
            BundlePool bundlePool = new BundlePool(
                blockTreeSub,
                Substitute.For<IBundleSimulator>(),
                null, 
                timestamper,
                mevConfig, 
                LimboLogs.Instance);
            SortedList<MevBundle, MevBundle> bundleList = 
                new SortedList<MevBundle, MevBundle>(CompareMevBundleByBlock.Default.ThenBy(CompareMevBundleByMinTimestamp.Default));

            List<MevBundle> bundleTest = new List<MevBundle>();
            for (int i = 1; i > 0; i--)
            {
                MevBundle newBundle = new MevBundle(txs, i, 0, 0);
                bundleTest.Add(newBundle);
            }

            MevBundle aheadOf1 = new MevBundle(txs, 1, timestamp + 200, timestamp + 300);
            bundleTest.Add(aheadOf1);

            MevBundle aheadOf4 = new MevBundle(txs, 4, timestamp + 300, timestamp + 400); 
            bundleTest.Add(aheadOf4);
            
            //if not greater than capacity, don't do anything
            //if greater than capacity, add the bundle; if the bundle is at the end, expect false from add Bundle; else expect true 
            int count = 0;
            foreach (MevBundle bundle in bundleTest)
            {
                IEnumerable<Keccak> bundleHashes = bundleList.Select(bundle => bundle.Key.Hash);
                bool bundleHashInBundleList = bundleHashes.Contains(bundle.Hash); //using fact that new bundle has same comparative value
                if (count < capacity && !bundleHashInBundleList) //if we don't have any dups and we are not at capacity, add the bundle
                {
                    bundleList.Add(bundle, bundle);
                    bundlePool.AddBundle(bundle).Should().Be(true);
                }
                else if (bundleHashInBundleList) //if two blocks have same hash, keep one with lower min timestamp
                {
                    IEnumerable<MevBundle> bundleHashEquals = bundleList.Where(b => b.Key.Hash == bundle.Hash).Select(b => b.Key);
                    //MevBundle bundleOut = bundleList.TryGetValue(bundle, out MevBundle? bundleOut);
                    MevBundle? bundleOut = null; //just a placeholder
                    foreach (MevBundle b in bundleHashEquals)
                    {
                        bundleOut = b;
                        break;
                    }
                    if ((bundleOut?.MinTimestamp < bundle.MinTimestamp) &&
                        (bundle.MaxTimestamp > timestamp) && 
                        (bundle.MinTimestamp <= bundle.MaxTimestamp) && 
                        (bundle.MinTimestamp <= timestamp + mevConfig.BundleHorizon)) //don't need to check for 0 because we are only replacing if min timestamp higher and lowest number is already 0 
                    {
                        bundleList.Remove(bundleOut);
                        bundleList.Add(bundle, bundle);
                        bundlePool.AddBundle(bundle).Should().Be(true);
                    }
                    else
                    {
                        bundlePool.AddBundle(bundle).Should().Be(false); 
                    }
                }
                else //if bundle is the lowest of the current elements, and we are at capacity, don't add it
                {
                    bundleList.Add(bundle, bundle);
                    if (bundleList.IndexOfKey(bundle) == (bundleList.Count - 1))
                    {
                        bundlePool.AddBundle(bundle).Should().Be(false);
                    }
                    else
                    {
                        bundlePool.AddBundle(bundle).Should().Be(true);
                    }
                    bundleList.RemoveAt(bundleList.Count - 1);
                }

                count++;
            }
            


            /*for (int i = 0; i < outputList.Count - 1; i++)
            {
                if (outputList[i].Key == outputList[i + 1].Key)
                {
                    outputList[i].MinTimestamp.CompareTo(outputList[i + 1].MinTimestamp).Should().BeOneOf(-1, 0);
                }
                else if (outputList[i].Key > BestBlockNumber && outputList[i + 1].Key > BestBlockNumber)
                {
                    outputList[i].Key.CompareTo(outputList[i + 1].MinTimestamp).Should().BeOneOf(-1, 0);
                }
                else
                {
                    outputList[i].Key.CompareTo(outputList[i + 1].MinTimestamp).Should().BeOneOf(1, 0);
                }
            }*/
        }
        
        public record BundleTest(long Block, ulong TestTimestamp, int ExpectedCount, int ExpectedRemaining, Action<BundlePool>? Action = null);
        
    }
}