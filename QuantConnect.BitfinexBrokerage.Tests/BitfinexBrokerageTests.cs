/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using NUnit.Framework;
using QuantConnect.Brokerages.Bitfinex;
using QuantConnect.Configuration;
using Moq;
using QuantConnect.Brokerages;
using QuantConnect.Tests.Common.Securities;
using QuantConnect.Lean.Engine.DataFeeds;

namespace QuantConnect.Tests.Brokerages.Bitfinex
{
    [TestFixture]
    public partial class BitfinexBrokerageTests : BrokerageTests
    {
        /// <summary>
        /// Creates the brokerage under test and connects it
        /// </summary>
        /// <param name="orderProvider"></param>
        /// <param name="securityProvider"></param>
        /// <returns></returns>
        protected override IBrokerage CreateBrokerage(IOrderProvider orderProvider, ISecurityProvider securityProvider)
        {
            var securities = new SecurityManager(new TimeKeeper(DateTime.UtcNow, TimeZones.NewYork))
            {
                {Symbol, SecurityProvider.GetSecurity(Symbol)}
            };

            var transactions = new SecurityTransactionManager(null, securities);
            transactions.SetOrderProcessor(new FakeOrderProcessor());
            var algorithmSettings = new AlgorithmSettings();

            var algorithm = new Mock<IAlgorithm>();
            algorithm.Setup(a => a.Transactions).Returns(transactions);
            algorithm.Setup(a => a.BrokerageModel).Returns(new BitfinexBrokerageModel());
            algorithm.Setup(a => a.Portfolio).Returns(new SecurityPortfolioManager(securities, transactions, algorithmSettings));

            return new BitfinexBrokerage(
                    Config.Get("bitfinex-api-key"),
                    Config.Get("bitfinex-api-secret"),
                    algorithm.Object,
                    new AggregationManager(),
                    null
                );
        }

        /// <summary>
        /// Gets the symbol to be traded, must be shortable
        /// </summary>
        protected override Symbol Symbol => StaticSymbol;
        private static Symbol StaticSymbol => Symbol.Create("ETHUSD", SecurityType.Crypto, Market.Bitfinex);

        /// <summary>
        /// Gets the security type associated with the <see cref="BrokerageTests.Symbol" />
        /// </summary>
        protected override SecurityType SecurityType => SecurityType.Crypto;

        /// <summary>
        /// Gets the current market price of the specified security
        /// </summary>
        protected override decimal GetAskPrice(Symbol symbol)
        {
            var tick = ((BitfinexBrokerage)Brokerage).GetTick(symbol);
            return tick.AskPrice;
        }

        /// <summary>
        /// Returns whether or not the brokers order methods implementation are async
        /// </summary>
        protected override bool IsAsync() => true;

        /// <summary>
        /// Returns whether or not the brokers order cancel method implementation is async
        /// </summary>
        protected override bool IsCancelAsync() => true;

        /// <summary>
        /// Gets the default order quantity
        /// </summary>
        protected override decimal GetDefaultQuantity() => 0.04m;

        [Explicit("Ignore a test")]
        public override void CancelOrders(OrderTestParameters parameters)
        {
            base.CancelOrders(parameters);
        }

        [Explicit("Ignore a test")]
        public override void LongFromZero(OrderTestParameters parameters)
        {
            base.LongFromZero(parameters);
        }

        [Explicit("Ignore a test")]
        public override void CloseFromLong(OrderTestParameters parameters)
        {
            base.CloseFromLong(parameters);
        }

        [Explicit("Ignore a test")]
        public override void ShortFromZero(OrderTestParameters parameters)
        {
            base.ShortFromZero(parameters);
        }

        [Explicit("Ignore a test")]
        public override void CloseFromShort(OrderTestParameters parameters)
        {
            base.CloseFromShort(parameters);
        }

        [Explicit("Ignore a test")]
        public override void ShortFromLong(OrderTestParameters parameters)
        {
            base.ShortFromLong(parameters);
        }

        [Explicit("Ignore a test")]
        public override void LongFromShort(OrderTestParameters parameters)
        {
            base.LongFromShort(parameters);
        }

        [Test]
        public override void GetAccountHoldings()
        {
            Assert.IsEmpty(Brokerage.GetAccountHoldings());
        }
    }
}
