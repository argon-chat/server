//namespace Argon.Grains;

//using Sfu;
//using Shared;
//using InviteCodeEntity = Entities.InviteCodeEntity;

//public class MeetGrainState
//{
//    public string         Title     { get; set; } = "Meet";
//    public DateTimeOffset StartTime { get; set; } = DateTimeOffset.UtcNow;
//}

//public class MeetGrain(
//    [PersistentState("meet-grains", IUserSessionGrain.StorageId)]
//    IPersistentState<MeetGrainState> state,
//    IDbContextFactory<ApplicationDbContext> context, 
//    IArgonSelectiveForwardingUnit sfu, 
//    IOptions<SfuFeatureSettings> sfuOptions) : Grain, IMeetGrain
//{
//    public override Task OnActivateAsync(CancellationToken cancellationToken)
//        => state.ReadStateAsync(cancellationToken);

//    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
//        => state.WriteStateAsync(cancellationToken);

//    public async Task<Either<JoinMeetResponse, MeetJoinError>> JoinAsync(string userName)
//    {
//        if (InviteCodeEntity.TryParseInviteCode(this.GetPrimaryKeyString(), out var inviteId))
//        {
//            await using var ctx = await context.CreateDbContextAsync();

//            var id = InviteCodeEntity.EncodeToUlong(this.GetPrimaryKeyString());

//            var inviteCode = await ctx.MeetInviteLinks.FirstOrDefaultAsync(x => x.Id.Equals(id));

//            if (inviteCode?.AssociatedChannelId is null || inviteCode.AssociatedServerId is null)
//                return MeetJoinError.NO_LINK_EXIST;

//            var token = await sfu.IssueAuthorizationTokenForMeetAsync(userName, new ArgonChannelId(new ArgonServerId(inviteCode.AssociatedServerId.Value), inviteCode.AssociatedChannelId.Value), SfuPermission.DefaultUser);

//            return new JoinMeetResponse(token, new RtcEndpoint(sfuOptions.Value.Url, []),
//                new MeetInfo(state.State.Title, state.State.StartTime.ToArgonTimeSeconds(), this.GetPrimaryKeyString()));
//        }
//        else
//        {
//            var token = await sfu.IssueAuthorizationTokenForMeetAsync(userName, new ArgonMeetId(this.GetPrimaryKeyString()), SfuPermission.DefaultUser);

//            return new JoinMeetResponse(token, new RtcEndpoint(sfuOptions.Value.Url, []),
//                new MeetInfo(state.State.Title, state.State.StartTime.ToArgonTimeSeconds(), this.GetPrimaryKeyString()));
//        }
//    }

//    public Task<string> CreateMeetingLinkAsync()
//        => Task.FromResult(this.GetPrimaryKeyString());

//    public Task SetDefaultPermissionsAsync(long permissions)
//        => Task.CompletedTask;

//    public async Task<string> BeginRecordAsync()
//        => await sfu.BeginRecordAsync(new ArgonMeetId(this.GetPrimaryKeyString()));

//    public Task<string> EndRecordAsync(string egressId)
//        => throw new NotImplementedException();

//    public Task MuteParticipantAsync(Guid participantId, bool isMuted)
//        => throw new NotImplementedException();

//    public Task DisableVideoAsync(Guid participantId, bool isDisabled)
//        => throw new NotImplementedException();
//}