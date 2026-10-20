using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Game_Store_API.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Game_Store_API.Controllers;

[ApiController]
[Route("api/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(16_384)]
public sealed class AuthController(AuthService auth, AccountRecoveryService recovery,
    ILogger<AuthController> logger) : ControllerBase
{
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        await recovery.RegisterAsync(request.Email.Trim().ToUpperInvariant(), request.Password, cancellationToken);
        return Accepted(new { message = "If eligible, you will receive an email verification code." });
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var token = await auth.LoginAsync(request.Email.Trim().ToUpperInvariant(), request.Password, cancellationToken);
        return token is null ? Unauthorized(new { message = "Invalid email or password." }) : Ok(token);
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me() => Ok(new { id = User.FindFirstValue("sub"), role = User.FindFirstValue("role") });

    [Authorize(Roles = AppRoles.Admin)]
    [EnableRateLimiting("auth")]
    [HttpPost("users/staff")]
    public async Task<IActionResult> AddStaff(RegisterRequest request, CancellationToken cancellationToken)
    {
        var id = await recovery.CreateStaffAsync(request.Email.Trim().ToUpperInvariant(), request.Password, cancellationToken);
        if (id is null) return Conflict(new { message = "An account with this email already exists." });
        HttpContext.Items["AuditTargetId"] = id.Value;
        logger.LogInformation("Administrator {ActorId} created employee {UserId}.", User.FindFirstValue("sub"), id);
        return StatusCode(StatusCodes.Status201Created, new
        {
            id, role = AppRoles.Employee, message = "Staff account created. Email verification is required before sign-in."
        });
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPut("users/{userId:guid}/role")]
    public async Task<IActionResult> SetRole(Guid userId, SetRoleRequest request, CancellationToken cancellationToken)
    {
        if (!await auth.SetRoleAsync(userId, request.Role, cancellationToken)) return NotFound();
        logger.LogInformation("Administrator {ActorId} assigned role {Role} to user {UserId}.",
            User.FindFirstValue("sub"), request.Role, userId);
        return NoContent();
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("resend-verification")]
    public async Task<IActionResult> ResendVerification(EmailRequest request, CancellationToken cancellationToken)
    {
        await recovery.RequestCodeAsync(request.Email.Trim().ToUpperInvariant(), false, cancellationToken);
        return Accepted(new { message = "If eligible, you will receive an email verification code." });
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("verify-email")]
    public async Task<IActionResult> VerifyEmail(CodeRequest request, CancellationToken cancellationToken)
    {
        var valid = await recovery.RedeemAsync(request.Email.Trim().ToUpperInvariant(), request.Code, null, cancellationToken);
        return valid ? Ok(new { message = "Email verified. You can now sign in." })
            : BadRequest(new { message = "Invalid or expired code." });
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(EmailRequest request, CancellationToken cancellationToken)
    {
        await recovery.RequestCodeAsync(request.Email.Trim().ToUpperInvariant(), true, cancellationToken);
        return Accepted(new { message = "If eligible, you will receive a password reset code." });
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var valid = await recovery.RedeemAsync(request.Email.Trim().ToUpperInvariant(), request.Code,
            request.NewPassword, cancellationToken);
        return valid ? Ok(new { message = "Password reset. Sign in with your new password." })
            : BadRequest(new { message = "Invalid or expired code." });
    }
}

public sealed record RegisterRequest(
    [Required, EmailAddress, MaxLength(254)] string Email,
    [Required, StringLength(128, MinimumLength = 10, ErrorMessage = "Password must be between 10 and 128 characters long.")]
    [RegularExpression(@"[\s\S]*[^\p{L}\p{N}\s][\s\S]*",
        ErrorMessage = "Password must contain at least one special character (for example !, @, #, or $).")]
    string Password);

public sealed record LoginRequest(
    [Required, EmailAddress, MaxLength(254)] string Email,
    [Required, StringLength(128)] string Password);

public sealed record EmailRequest([Required, EmailAddress, MaxLength(254)] string Email);

public sealed record SetRoleRequest([Required, RegularExpression("default|employee|admin")] string Role);

public sealed record CodeRequest(
    [Required, EmailAddress, MaxLength(254)] string Email,
    [Required, RegularExpression("[A-F0-9]{64}")] string Code);

public sealed record ResetPasswordRequest(
    [Required, EmailAddress, MaxLength(254)] string Email,
    [Required, RegularExpression("[A-F0-9]{64}")] string Code,
    [Required, StringLength(128, MinimumLength = 10, ErrorMessage = "Password must be between 10 and 128 characters long.")]
    [RegularExpression(@"[\s\S]*[^\p{L}\p{N}\s][\s\S]*",
        ErrorMessage = "Password must contain at least one special character (for example !, @, #, or $).")]
    string NewPassword);
