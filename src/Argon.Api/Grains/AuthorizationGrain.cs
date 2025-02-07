namespace Argon.Grains;

using Features.Otp;
using Orleans.Concurrency;
using Services;

[StatelessWorker]
public class AuthorizationGrain(
    IGrainFactory grainFactory,
    ILogger<AuthorizationGrain> logger,
    UserManagerService managerService,
    IPasswordHashingService passwordHashingService,
    IDbContextFactory<ApplicationDbContext> context) : Grain, IAuthorizationGrain
{
    public async Task<Either<string, AuthorizationError>> Authorize(UserCredentialsInput input, UserConnectionInfo connectionInfo)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Email == input.Email);
        if (user is null)
        {
            logger.LogWarning("Not found user '{email}'", input.Email);
            return AuthorizationError.BAD_CREDENTIALS;
        }

        var verified = passwordHashingService.VerifyPassword(input.Password, user);

        if (!verified)
        {
            logger.LogWarning("User '{email}' entered bad password, not matched", input.Email);
            return AuthorizationError.BAD_CREDENTIALS;
        }

        if (string.IsNullOrEmpty(input.OtpCode))
        {
            var otp = passwordHashingService.GenerateOtp(user.Id);
            user.OtpHash = otp.Hashed;
            ctx.Users.Update(user);
            await ctx.SaveChangesAsync();
            // TODO check latest send otp time (evade ddos)
            await grainFactory.GetGrain<IEmailManager>(Guid.NewGuid())
               .SendOtpCodeAsync(user.Email, otp.Code, TimeSpan.FromMinutes(15));
            logger.LogInformation("User '{email}' invoked a generate otp code", input.Email);
            return AuthorizationError.REQUIRED_OTP;
        }

        var userOtp = new OtpCode(input.OtpCode);

        if (!(user.OtpHash?.Equals(userOtp.Hashed) ?? false))
        {
            logger.LogError("User '{email}' entered invalid otp code {otp} {optHash}", input.Email, userOtp.Code, userOtp.Hashed);
            return AuthorizationError.BAD_OTP;
        }

        user.OtpHash = null;
        ctx.Users.Update(user);
        await ctx.SaveChangesAsync();
        return await GenerateJwt(user, Guid.NewGuid());
    }

    public async Task<Either<string, RegistrationError>> Register(NewUserCredentialsInput input, UserConnectionInfo connectionInfo)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Email == input.Email);
        if (user is not null)
        {
            logger.LogWarning("Email already registered '{email}'", input.Email);
            return RegistrationError.EMAIL_ALREADY_REGISTERED;
        }

        user = await ctx.Users.FirstOrDefaultAsync(u => u.Username == input.Username);
        if (user is not null)
        {
            logger.LogWarning("Username already registered '{username}'", input.Username);
            return RegistrationError.USERNAME_ALREADY_TAKEN;
        }

        // TODO check reserved username

        // TODO check sso email (mx records and etc)

        // TODO check region banned

        // TODO check banned emails
        var strategy = ctx.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await ctx.Database.BeginTransactionAsync();
            try
            {
                var userId = Guid.NewGuid();
                user = new User()
                {
                    AvatarFileId   = null,
                    CreatedAt      = DateTime.UtcNow,
                    Email          = input.Email,
                    Id             = userId,
                    Username       = input.Username,
                    PasswordDigest = passwordHashingService.HashPassword(input.Password),
                    PhoneNumber    = input.PhoneNumber,
                    DisplayName    = input.DisplayName,
                };
                await ctx.Users.AddAsync(user);

                var agreements = new UserAgreements()
                {
                    AgreeTOS                  = input.AgreeTos,
                    AllowedSendOptionalEmails = input.AgreeOptionalEmails,
                    UserId                    = userId
                };
                await ctx.UserAgreements.AddAsync(agreements);

                await ctx.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                throw;
            }
        });
        if (user is null)
            return RegistrationError.INTERNAL_ERROR;

        return await GenerateJwt(user, Guid.NewGuid());
    }

    private async Task<string> GenerateJwt(User User, Guid machineId) => await managerService.GenerateJwt(User.Id, machineId);
}