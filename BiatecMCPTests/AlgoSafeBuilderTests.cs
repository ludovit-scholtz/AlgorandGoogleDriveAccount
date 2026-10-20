using Algorand;
using Algorand.Algod;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Arc56.Generated.ludovit_scholtz.algo_safe.AlgoSafe_e28553fc;
using AVM.ClientGenerator.Core;
using BiatecMCP.BusinessLogic.AlgoSafe;

namespace BiatecMCPTests
{
    /// <summary>
    /// A fake node holding one real-hash AlgoSafe application and its boxes, so the builder's pre-checks and the reader's decoding run end to
    /// end (struct encoding, box keys, payload chunks) without a network. Simulation always succeeds with no extra requirements.
    /// </summary>
    internal sealed class FakeAlgoSafeChain : IAlgoSafeChainClient
    {
        public const ulong AppId = 500;
        private readonly Dictionary<string, byte[]> _boxes = new();
        private readonly Application _app;
        private readonly HttpClient _http = new() { BaseAddress = new Uri("https://localhost") };

        public FakeAlgoSafeChain(ulong nextProposalId = 1, ulong nextGroupId = 2, bool bootstrapped = true, bool paused = false)
        {
            Algod = new DefaultApi(_http);
            var program = ApprovalProgramBytes().GetAwaiter().GetResult();
            _app = new Application
            {
                Id = AppId,
                Params = new ApplicationParams
                {
                    Creator = Member1,
                    ApprovalProgram = new TEALProgram(program),
                    GlobalState = new List<TealKeyValue>
                    {
                        Kv("boot", bootstrapped ? 1UL : 0UL),
                        Kv("paused", paused ? 1UL : 0UL),
                        Kv("npid", nextProposalId),
                        Kv("ngid", nextGroupId),
                        Kv("vapp", 77)
                    }
                }
            };
        }

        public static readonly Address Member1 = Address.ForApplication(901);
        public static readonly Address Member2 = Address.ForApplication(902);
        public static readonly Address Outsider = Address.ForApplication(903);
        public static readonly Address Receiver = Address.ForApplication(904);

        public ulong LastRound { get; set; } = 1000;

        public DefaultApi Algod { get; }

        private static TealKeyValue Kv(string key, ulong value) => new()
        {
            Key = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(key)),
            Value = new TealValue { Type = 2, Uint = value }
        };

        private static async Task<byte[]> ApprovalProgramBytes()
        {
            using var http = new HttpClient { BaseAddress = new Uri("https://localhost") };
            var parameters = new TransactionParametersResponse { MinFee = 1000, LastRound = 100, GenesisId = "testnet-v1.0", GenesisHash = new byte[32], ConsensusVersion = "future" };
            var created = await new AlgoSafeProxy(new DefaultApi(http), 0) { TransParamsOverride = parameters }
                .CreateApplication_Transactions("n", 1UL, new Algorand.Algod.Model.Account { Address = Member1 }, null, "", 1000, _tx_callType: OnCompleteType.CreateApplication);
            return ((ApplicationCreateTransaction)created[0]).ApprovalProgram.Bytes;
        }

        // ───────── seeding ─────────

        private void Put(byte[] name, byte[] value) => _boxes[Convert.ToHexString(name)] = value;

        public FakeAlgoSafeChain WithGroup(ulong id, ulong threshold, ulong memberCount, ulong allowedActions = AlgoSafeConstants.ActAll, params Address[] members)
        {
            Put(AlgoSafeBoxNames.Group(id), new AlgoSafeProxy.Structs.SignerGroup
            {
                Name = "Admins", Threshold = threshold, MemberCount = memberCount, AdminPrivileges = AlgoSafeConstants.PrivAll,
                AllowedActions = allowedActions, Active = 1, MembershipEpoch = 3
            }.ToByteArray());
            foreach (var member in members)
            {
                Put(AlgoSafeBoxNames.Member(id, member), new AlgoSafeProxy.Structs.Member { AccountType = 1, Label = "m", Addr = member }.ToByteArray());
            }

            return this;
        }

        public FakeAlgoSafeChain WithProposal(
            ulong id, ulong groupId, ulong status, IReadOnlyList<AlgoSafePayloadEntry>? payload, ulong expiryRound = 5000, ulong approvals = 0, ulong threshold = 2, ulong payloadType = AlgoSafeConstants.PayloadTypeTransactionGroup, ulong totalTxns = ulong.MaxValue, params Address[] approvers)
        {
            var count = (ulong)(payload?.Count ?? 0);
            Put(AlgoSafeBoxNames.Proposal(id), new AlgoSafeProxy.Structs.Proposal
            {
                GroupId = groupId, Status = status, PayloadType = payloadType, ApprovalsCount = approvals, Threshold = threshold, ExpiryRound = expiryRound,
                Proposer = Member1, NumPayloads = payload == null ? 0UL : 1UL, EpochAtCreation = 3, PayloadVersion = 1, TotalTxns = totalTxns == ulong.MaxValue ? count : totalTxns
            }.ToByteArray());
            if (payload != null)
            {
                Put(AlgoSafeBoxNames.TransactionGroup(id, 1), AlgoSafePayloadCodec.EncodeEnvelopeArray(payload));
            }

            foreach (var approver in approvers)
            {
                Put(AlgoSafeBoxNames.Approval(id, approver), new byte[] { 1 });
            }

            return this;
        }

        public static AlgoSafePayloadEntry Payment(Address receiver, ulong amount = 1000, bool close = false) =>
            new(AlgoSafeConstants.TxPayment, AlgoSafePayloadCodec.EncodePayment(AlgoSafePayloadCodec.ZeroAddress, receiver, amount, close ? 1UL : 0UL, close ? Receiver : AlgoSafePayloadCodec.ZeroAddress, "note"));

        // ───────── IAlgoSafeChainClient ─────────

        public Task<TransactionParametersResponse> GetTransactionParamsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransactionParametersResponse { MinFee = 1000, LastRound = LastRound, GenesisId = "testnet-v1.0", GenesisHash = new byte[32], ConsensusVersion = "future" });

        public Task<Application?> GetApplicationAsync(ulong appId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Application?>(appId == AppId ? _app : null);

        public Task<byte[]?> GetBoxAsync(ulong appId, byte[] name, CancellationToken cancellationToken = default) =>
            Task.FromResult(_boxes.TryGetValue(Convert.ToHexString(name), out var value) ? value : null);

        public Task<IReadOnlyList<byte[]>> ListBoxNamesAsync(ulong appId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<byte[]>>(_boxes.Keys.Select(Convert.FromHexString).ToList());

        public Task<SimulateTransactionGroupResult> SimulateAsync(IReadOnlyList<Transaction> group, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SimulateTransactionGroupResult
            {
                TxnResults = group.Select(_ => new SimulateTransactionResult { ExecTrace = new SimulationTransactionExecTrace() }).ToList()
            });

        public Task<IReturnableTransaction?> GetPendingTransactionAsync(string transactionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReturnableTransaction?>(null);

        public Task<ulong> GetBalanceAsync(Address address, CancellationToken cancellationToken = default) => Task.FromResult(5_000_000UL);

        public void Dispose() => _http.Dispose();
    }

    [TestFixture]
    public class AlgoSafeBuilderTests
    {
        private static AlgoSafeTransactionBuilder BuilderFor(FakeAlgoSafeChain chain) => new(chain, new AlgoSafeReader(chain));

        private static FakeAlgoSafeChain SafeWithTwoOfTwoGroup() =>
            new FakeAlgoSafeChain().WithGroup(1, 2, 2, AlgoSafeConstants.ActAll, FakeAlgoSafeChain.Member1, FakeAlgoSafeChain.Member2);

        private static IReadOnlyList<AlgoSafeTransferRequest> OneTransfer(ulong amount = 1000) =>
            new[] { new AlgoSafeTransferRequest(FakeAlgoSafeChain.Receiver.EncodeAsString(), amount) };

        private static async Task<AlgoSafeException> Fails(Func<Task> action)
        {
            var ex = await Assert.ThrowsAsync<AlgoSafeException>(() => action());
            return ex!;
        }

        // ───────── propose ─────────

        [Test]
        public async Task Propose_ByAMember_BuildsOneApplicationCall()
        {
            var step = await BuilderFor(SafeWithTwoOfTwoGroup()).BuildProposeAsync(FakeAlgoSafeChain.Member1, FakeAlgoSafeChain.AppId, 1, OneTransfer(), 1000, false);

            Assert.That(step.UnsignedTransactions, Has.Count.EqualTo(1));
            Assert.That(step.TotalFeeMicroAlgo, Is.EqualTo(1000UL));
        }

        [Test]
        public async Task Propose_ByANonMember_IsRefusedWithAClearError()
        {
            var ex = await Fails(() => BuilderFor(SafeWithTwoOfTwoGroup()).BuildProposeAsync(FakeAlgoSafeChain.Outsider, FakeAlgoSafeChain.AppId, 1, OneTransfer(), 1000, false));

            Assert.That(ex.ErrorType, Is.EqualTo("NotAMember"));
        }

        [Test]
        public async Task Propose_WithExecuteNow_NeedsAOneApprovalGroup()
        {
            var ex = await Fails(() => BuilderFor(SafeWithTwoOfTwoGroup()).BuildProposeAsync(FakeAlgoSafeChain.Member1, FakeAlgoSafeChain.AppId, 1, OneTransfer(), 1000, true));

            Assert.That(ex.ErrorType, Is.EqualTo("ThresholdNotOne"));
        }

        [Test]
        public async Task Propose_UnknownGroup_NotBootstrapped_Paused_AreEachRefused()
        {
            Assert.That((await Fails(() => BuilderFor(SafeWithTwoOfTwoGroup()).BuildProposeAsync(FakeAlgoSafeChain.Member1, FakeAlgoSafeChain.AppId, 9, OneTransfer(), 1000, false))).ErrorType, Is.EqualTo("GroupNotFound"));
            Assert.That((await Fails(() => BuilderFor(new FakeAlgoSafeChain(bootstrapped: false)).BuildProposeAsync(FakeAlgoSafeChain.Member1, FakeAlgoSafeChain.AppId, 1, OneTransfer(), 1000, false))).ErrorType, Is.EqualTo("NotBootstrapped"));
            Assert.That((await Fails(() => BuilderFor(new FakeAlgoSafeChain(paused: true)).BuildProposeAsync(FakeAlgoSafeChain.Member1, FakeAlgoSafeChain.AppId, 1, OneTransfer(), 1000, false))).ErrorType, Is.EqualTo("SafePaused"));
        }

        [Test]
        public async Task Propose_InvalidInputs_AreRefusedBeforeAnyNodeWork()
        {
            var builder = BuilderFor(SafeWithTwoOfTwoGroup());
            var member = FakeAlgoSafeChain.Member1;

            Assert.That((await Fails(() => builder.BuildProposeAsync(member, FakeAlgoSafeChain.AppId, 1, OneTransfer(), 10, false))).ErrorType, Is.EqualTo("InvalidRequest"), "expiry below the minimum");
            Assert.That((await Fails(() => builder.BuildProposeAsync(member, FakeAlgoSafeChain.AppId, 1, OneTransfer(0), 1000, false))).ErrorType, Is.EqualTo("InvalidRequest"), "zero amount");
            Assert.That((await Fails(() => builder.BuildProposeAsync(member, FakeAlgoSafeChain.AppId, 1, new[] { new AlgoSafeTransferRequest(new string('A', 58), 1) }, 1000, false))).ErrorType, Is.EqualTo("InvalidRequest"), "bad receiver");
            Assert.That((await Fails(() => builder.BuildProposeAsync(member, FakeAlgoSafeChain.AppId, 1, new[] { new AlgoSafeTransferRequest(AlgoSafePayloadCodec.ZeroAddress.EncodeAsString(), 1) }, 1000, false))).ErrorType, Is.EqualTo("InvalidRequest"), "zero address receiver");
            Assert.That((await Fails(() => builder.BuildProposeAsync(member, FakeAlgoSafeChain.AppId, 1, new[] { new AlgoSafeTransferRequest(FakeAlgoSafeChain.Receiver.EncodeAsString(), 1, 0, new string('x', 2000)) }, 1000, false))).ErrorType, Is.EqualTo("InvalidRequest"), "note too long");
        }

        [Test]
        public async Task Propose_TooManyTransfers_FailsWithProposalTooLarge()
        {
            var many = Enumerable.Range(0, 16).Select(_ => new AlgoSafeTransferRequest(FakeAlgoSafeChain.Receiver.EncodeAsString(), 1, 0, new string('n', 40))).ToList();

            var ex = await Fails(() => BuilderFor(SafeWithTwoOfTwoGroup()).BuildProposeAsync(FakeAlgoSafeChain.Member1, FakeAlgoSafeChain.AppId, 1, many, 1000, false));

            Assert.That(ex.ErrorType, Is.EqualTo("ProposalTooLarge"));
        }

        [Test]
        public async Task Propose_AssetTransfer_NeedsTheGroupToAllowIt()
        {
            var chain = new FakeAlgoSafeChain().WithGroup(1, 1, 1, AlgoSafeConstants.ActPay, FakeAlgoSafeChain.Member1);

            var ex = await Fails(() => BuilderFor(chain).BuildProposeAsync(
                FakeAlgoSafeChain.Member1, FakeAlgoSafeChain.AppId, 1, new[] { new AlgoSafeTransferRequest(FakeAlgoSafeChain.Receiver.EncodeAsString(), 5, 31566704) }, 1000, false));

            Assert.That(ex.ErrorType, Is.EqualTo("ActionNotAllowed"));
        }

        // ───────── approve ─────────

        [Test]
        public async Task Approve_ReviewableActiveProposal_BuildsTheApprovalAndDescribesWhatItSends()
        {
            var chain = SafeWithTwoOfTwoGroup().WithProposal(1, 1, AlgoSafeConstants.StatusActive, new[] { FakeAlgoSafeChain.Payment(FakeAlgoSafeChain.Receiver, 1234) });

            var step = await BuilderFor(chain).BuildApproveAsync(FakeAlgoSafeChain.Member2, FakeAlgoSafeChain.AppId, 1);

            Assert.That(step.UnsignedTransactions, Has.Count.EqualTo(1));
            Assert.That(step.Summary, Does.Contain("1234 microAlgo to " + FakeAlgoSafeChain.Receiver.EncodeAsString()));
        }

        [Test]
        public async Task Approve_RefusesEachNonApprovableCase()
        {
            var good = new[] { FakeAlgoSafeChain.Payment(FakeAlgoSafeChain.Receiver) };
            var member = FakeAlgoSafeChain.Member2;

            async Task<string> Run(FakeAlgoSafeChain chain) => (await Fails(() => BuilderFor(chain).BuildApproveAsync(member, FakeAlgoSafeChain.AppId, 1))).ErrorType;

            Assert.That(await Run(SafeWithTwoOfTwoGroup().WithProposal(1, 1, AlgoSafeConstants.StatusExecuted, null)), Is.EqualTo("NotApprovable"), "already executed (payload pruned)");
            Assert.That(await Run(SafeWithTwoOfTwoGroup().WithProposal(1, 1, AlgoSafeConstants.StatusActive, good, expiryRound: 10)), Is.EqualTo("ProposalExpired"));
            Assert.That(await Run(SafeWithTwoOfTwoGroup().WithProposal(1, 1, AlgoSafeConstants.StatusActive, new[] { FakeAlgoSafeChain.Payment(FakeAlgoSafeChain.Receiver, close: true) })), Is.EqualTo("UnsupportedProposal"), "close-out");
            Assert.That(await Run(SafeWithTwoOfTwoGroup().WithProposal(1, 1, AlgoSafeConstants.StatusActive, good, totalTxns: 3)), Is.EqualTo("UnsupportedProposal"), "unreadable part");
            Assert.That(await Run(SafeWithTwoOfTwoGroup().WithProposal(1, 1, AlgoSafeConstants.StatusActive, good, payloadType: AlgoSafeConstants.PayloadTypeAdmin)), Is.EqualTo("UnsupportedProposal"), "governance change");
            Assert.That(await Run(SafeWithTwoOfTwoGroup().WithProposal(1, 1, AlgoSafeConstants.StatusActive, good, approvers: member)), Is.EqualTo("AlreadyApproved"));
            Assert.That(await Run(SafeWithTwoOfTwoGroup().WithProposal(1, 1, AlgoSafeConstants.StatusActive, new[] { new AlgoSafePayloadEntry(32, new byte[] { 1 }) })), Is.EqualTo("UnsupportedProposal"), "undecoded kind");

            Assert.That((await Fails(() => BuilderFor(SafeWithTwoOfTwoGroup().WithProposal(1, 1, AlgoSafeConstants.StatusActive, good)).BuildApproveAsync(FakeAlgoSafeChain.Outsider, FakeAlgoSafeChain.AppId, 1))).ErrorType, Is.EqualTo("NotAMember"));
        }

        // ───────── execute ─────────

        [Test]
        public async Task Execute_ReadyProposal_Builds_ButAnActiveOneIsNotReady()
        {
            var ready = SafeWithTwoOfTwoGroup().WithProposal(1, 1, AlgoSafeConstants.StatusReady, new[] { FakeAlgoSafeChain.Payment(FakeAlgoSafeChain.Receiver) }, approvals: 2);
            var step = await BuilderFor(ready).BuildExecuteAsync(FakeAlgoSafeChain.Member1, FakeAlgoSafeChain.AppId, 1);
            Assert.That(step.UnsignedTransactions, Is.Not.Empty);

            var active = SafeWithTwoOfTwoGroup().WithProposal(1, 1, AlgoSafeConstants.StatusActive, new[] { FakeAlgoSafeChain.Payment(FakeAlgoSafeChain.Receiver) }, approvals: 1);
            Assert.That((await Fails(() => BuilderFor(active).BuildExecuteAsync(FakeAlgoSafeChain.Member1, FakeAlgoSafeChain.AppId, 1))).ErrorType, Is.EqualTo("NotReady"));
        }

        [Test]
        public async Task Execute_OnAPausedSafe_IsRefused()
        {
            var chain = new FakeAlgoSafeChain(paused: true).WithGroup(1, 2, 2, AlgoSafeConstants.ActAll, FakeAlgoSafeChain.Member1)
                .WithProposal(1, 1, AlgoSafeConstants.StatusReady, new[] { FakeAlgoSafeChain.Payment(FakeAlgoSafeChain.Receiver) });

            Assert.That((await Fails(() => BuilderFor(chain).BuildExecuteAsync(FakeAlgoSafeChain.Member1, FakeAlgoSafeChain.AppId, 1))).ErrorType, Is.EqualTo("SafePaused"));
        }

        // ───────── bootstrap ─────────

        [Test]
        public async Task Bootstrap_OnlyTheCreatorMay_AndOnlyOnce()
        {
            var plan = new AlgoSafeBootstrapPlan("Admins");

            Assert.That((await Fails(() => BuilderFor(new FakeAlgoSafeChain(bootstrapped: false)).BuildBootstrapAsync(FakeAlgoSafeChain.Outsider, FakeAlgoSafeChain.AppId, 2_000_000, plan))).ErrorType, Is.EqualTo("NotCreator"));
            Assert.That((await Fails(() => BuilderFor(new FakeAlgoSafeChain()).BuildBootstrapAsync(FakeAlgoSafeChain.Member1, FakeAlgoSafeChain.AppId, 2_000_000, plan))).ErrorType, Is.EqualTo("AlreadyBootstrapped"));
            Assert.That((await Fails(() => BuilderFor(new FakeAlgoSafeChain(bootstrapped: false)).BuildBootstrapAsync(FakeAlgoSafeChain.Member1, FakeAlgoSafeChain.AppId, 0, plan))).ErrorType, Is.EqualTo("InvalidRequest"));
        }

        [Test]
        public async Task Bootstrap_SimplePlan_IsDepositPlusOneCall_WhileLimitsOrMembersNeedTheGroupPath()
        {
            var chain = new FakeAlgoSafeChain(bootstrapped: false);
            var builder = BuilderFor(chain);
            var creator = FakeAlgoSafeChain.Member1;

            var simple = await builder.BuildBootstrapAsync(creator, FakeAlgoSafeChain.AppId, 2_000_000, new AlgoSafeBootstrapPlan("Admins"));
            Assert.That(simple.UnsignedTransactions, Has.Count.EqualTo(2), "deposit + bootstrap");

            var limited = await builder.BuildBootstrapAsync(creator, FakeAlgoSafeChain.AppId, 2_000_000, new AlgoSafeBootstrapPlan("Admins", DailyLimit: 5_000_000));
            Assert.That(limited.UnsignedTransactions, Has.Count.EqualTo(3), "deposit + bootstrapGroup + finalize");

            var multi = await builder.BuildBootstrapAsync(creator, FakeAlgoSafeChain.AppId, 2_000_000,
                new AlgoSafeBootstrapPlan("Treasury", new[] { (creator.EncodeAsString(), "a"), (FakeAlgoSafeChain.Member2.EncodeAsString(), "b") }, Threshold: 2));
            Assert.That(multi.UnsignedTransactions, Has.Count.EqualTo(3));
        }

        [Test]
        public async Task Bootstrap_InvalidGroups_AreRefused()
        {
            var builder = BuilderFor(new FakeAlgoSafeChain(bootstrapped: false));
            var creator = FakeAlgoSafeChain.Member1.EncodeAsString();
            var other = FakeAlgoSafeChain.Member2.EncodeAsString();

            async Task<string> Run(AlgoSafeBootstrapPlan plan) =>
                (await Fails(() => builder.BuildBootstrapAsync(FakeAlgoSafeChain.Member1, FakeAlgoSafeChain.AppId, 2_000_000, plan))).ErrorType;

            Assert.That(await Run(new AlgoSafeBootstrapPlan("g", new[] { (creator, "a"), (other, "b") }, Threshold: 3)), Is.EqualTo("InvalidRequest"), "threshold above member count");
            Assert.That(await Run(new AlgoSafeBootstrapPlan("g", new[] { (creator, "a"), (creator, "b") }, Threshold: 1)), Is.EqualTo("InvalidRequest"), "duplicate member");
            Assert.That(await Run(new AlgoSafeBootstrapPlan("g", Enumerable.Range(0, 7).Select(i => (Address.ForApplication((ulong)(2000 + i)).EncodeAsString(), "m")).ToList(), Threshold: 1)), Is.EqualTo("InvalidRequest"), "more than 6 members");
            Assert.That(await Run(new AlgoSafeBootstrapPlan(new string('g', 100))), Is.EqualTo("InvalidRequest"), "name too long");
        }

        [Test]
        public async Task Execute_SummaryListsWhatIsSent_AndWarnsAboutCloseOuts()
        {
            var chain = SafeWithTwoOfTwoGroup().WithProposal(1, 1, AlgoSafeConstants.StatusReady, new[] { FakeAlgoSafeChain.Payment(FakeAlgoSafeChain.Receiver, 777, close: true) });

            var step = await BuilderFor(chain).BuildExecuteAsync(FakeAlgoSafeChain.Member1, FakeAlgoSafeChain.AppId, 1);

            Assert.That(step.Summary, Does.Contain("777 microAlgo to " + FakeAlgoSafeChain.Receiver.EncodeAsString()));
            Assert.That(step.Summary, Does.Contain("WARNING: includes a close-out"));
        }

        [Test]
        public async Task GetProposal_AbsurdProposalIds_AreNotFoundRatherThanOverflowing()
        {
            var reader = new AlgoSafeReader(SafeWithTwoOfTwoGroup());

            var ex = await Assert.ThrowsAsync<AlgoSafeException>(() => reader.GetProposalAsync(FakeAlgoSafeChain.AppId, ulong.MaxValue, null));

            Assert.That(ex!.ErrorType, Is.EqualTo("ProposalNotFound"));
        }

        [Test]
        public async Task Bootstrap_ThresholdAboveOneWithoutMembers_IsRejectedNotDropped()
        {
            var builder = BuilderFor(new FakeAlgoSafeChain(bootstrapped: false));

            var ex = await Assert.ThrowsAsync<AlgoSafeException>(() =>
                builder.BuildBootstrapAsync(FakeAlgoSafeChain.Member1, FakeAlgoSafeChain.AppId, 2_000_000, new AlgoSafeBootstrapPlan("Admins", Threshold: 3)));

            Assert.That(ex!.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        // ───────── reader ─────────

        [Test]
        public async Task GetSafe_ListsGroupsWithMembers_AndFlagsAnUnsupportedApp()
        {
            var chain = SafeWithTwoOfTwoGroup();

            var view = await new AlgoSafeReader(chain).GetSafeAsync(FakeAlgoSafeChain.AppId);

            Assert.That(view.Supported, Is.True);
            Assert.That(view.ContractVersion, Is.EqualTo("v3.4.0"));
            Assert.That(view.Groups, Has.Count.EqualTo(1));
            Assert.That(view.Groups[0].Threshold, Is.EqualTo(2UL));
            Assert.That(view.Groups[0].Members.Select(m => m.Address), Is.EquivalentTo(new[] { FakeAlgoSafeChain.Member1.EncodeAsString(), FakeAlgoSafeChain.Member2.EncodeAsString() }));
            Assert.That(view.Groups[0].AllowedActions, Does.Contain("payment"));
        }

        [Test]
        public async Task ListProposals_FiltersByStatus_NewestFirst()
        {
            var pay = new[] { FakeAlgoSafeChain.Payment(FakeAlgoSafeChain.Receiver) };
            var chain = new FakeAlgoSafeChain(nextProposalId: 4).WithGroup(1, 2, 2, AlgoSafeConstants.ActAll, FakeAlgoSafeChain.Member1)
                .WithProposal(1, 1, AlgoSafeConstants.StatusExecuted, null)
                .WithProposal(2, 1, AlgoSafeConstants.StatusActive, pay)
                .WithProposal(3, 1, AlgoSafeConstants.StatusReady, pay);
            var reader = new AlgoSafeReader(chain);

            var (open, truncated) = await reader.ListProposalsAsync(FakeAlgoSafeChain.AppId, "open", 10);
            Assert.That(open.Select(p => p.ProposalId), Is.EqualTo(new ulong[] { 3, 2 }));
            Assert.That(truncated, Is.False);

            var (executed, _) = await reader.ListProposalsAsync(FakeAlgoSafeChain.AppId, "executed", 10);
            Assert.That(executed.Select(p => p.ProposalId), Is.EqualTo(new ulong[] { 1 }));
        }

        [Test]
        public async Task GetProposal_ReportsCallerFlagsAndCanExecute()
        {
            var chain = SafeWithTwoOfTwoGroup().WithProposal(1, 1, AlgoSafeConstants.StatusActive, new[] { FakeAlgoSafeChain.Payment(FakeAlgoSafeChain.Receiver) }, approvals: 1, approvers: FakeAlgoSafeChain.Member1);
            var reader = new AlgoSafeReader(chain);

            var asApprover = await reader.GetProposalAsync(FakeAlgoSafeChain.AppId, 1, FakeAlgoSafeChain.Member1);
            Assert.That(asApprover.CallerHasApproved, Is.True);
            Assert.That(asApprover.CallerCanApprove, Is.False);
            Assert.That(asApprover.Approvers, Is.EqualTo(new[] { FakeAlgoSafeChain.Member1.EncodeAsString() }));

            var asOther = await reader.GetProposalAsync(FakeAlgoSafeChain.AppId, 1, FakeAlgoSafeChain.Member2);
            Assert.That(asOther.CallerCanApprove, Is.True);
            Assert.That(asOther.CanExecute, Is.False);
            Assert.That(asOther.Transactions.Single().Amount, Is.EqualTo(1000UL));
        }
    }
}
