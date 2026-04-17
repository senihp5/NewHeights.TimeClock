using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace NewHeights.TimeClock.Web.Controllers;

/// <summary>
/// Phase 8 student auth entry points. The student self check-in page
/// (/student/checkin) links to these controller actions because a Blazor page
/// cannot directly issue a scheme-specific challenge — the default is Entra,
/// which is wrong for students.
/// </summary>
[AllowAnonymous]
[Route("student")]
public class StudentAuthController : Controller
{
    /// <summary>
    /// Kicks off Google OAuth. Returns to /student/checkin after successful login.
    /// Safe to call when already authenticated — Google will just re-validate and
    /// redirect back.
    /// </summary>
    [HttpGet("sign-in")]
    public IActionResult SignIn(string? returnUrl = null)
    {
        var safeReturn = string.IsNullOrWhiteSpace(returnUrl) || !Url.IsLocalUrl(returnUrl)
            ? "/student/checkin"
            : returnUrl;

        var props = new AuthenticationProperties { RedirectUri = safeReturn };
        return Challenge(props, GoogleDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Signs the student out of the Google cookie session and redirects home.
    /// Does NOT touch the Entra cookie — staff accounts are unaffected.
    /// </summary>
    [HttpGet("sign-out")]
    public async Task<IActionResult> SignOutStudent()
    {
        await HttpContext.SignOutAsync(
            Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/");
    }
}
