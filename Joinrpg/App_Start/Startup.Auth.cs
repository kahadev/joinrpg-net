﻿using System;
using System.Collections.Generic;
using JoinRpg.Dal.Impl;
using JoinRpg.DataModel;
using JoinRpg.Web.Helpers;
using KatanaContrib.Security.VK;
using Microsoft.AspNet.Identity;
using Microsoft.AspNet.Identity.Owin;
using Microsoft.Owin;
using Microsoft.Owin.Security.Cookies;
using Microsoft.Owin.Security.Google;
using Owin;

namespace JoinRpg.Web
{
  public partial class Startup
  {
    // For more information on configuring authentication, please visit http://go.microsoft.com/fwlink/?LinkId=301864
    private void ConfigureAuth(IAppBuilder app)
    {
      // Configure the db context, user manager and signin manager to use a single instance per request
      app.CreatePerOwinContext(MyDbContext.Create);
      app.CreatePerOwinContext<ApplicationUserManager>(ApplicationUserManager.Create);
      app.CreatePerOwinContext<ApplicationSignInManager>(ApplicationSignInManager.Create);

      // Enable the application to use a cookie to store information for the signed in user
      // and to use a cookie to temporarily store information about a user logging in with a third party login provider
      // Configure the sign in cookie
      app.UseCookieAuthentication(new CookieAuthenticationOptions
      {
        AuthenticationType = DefaultAuthenticationTypes.ApplicationCookie,
        LoginPath = new PathString("/Account/Login"),
        Provider = new CookieAuthenticationProvider
        {
          // Enables the application to validate the security stamp when the user logs in.
          // This is a security feature which is used when you change a password or add an external login to your account.  
          OnValidateIdentity = SecurityStampValidator.OnValidateIdentity<ApplicationUserManager, User, int>
            (TimeSpan.FromDays(30),
              (manager, user) => user.GenerateUserIdentityAsync(manager),
              claimsIdentity => claimsIdentity.GetUserId<int>())
        }
      });
      app.UseExternalSignInCookie(DefaultAuthenticationTypes.ExternalCookie);

      // Uncomment the following lines to enable logging in with third party login providers
      //app.UseMicrosoftAccountAuthentication(
      //    clientId: "",
      //    clientSecret: "");

      //app.UseTwitterAuthentication(
      //   consumerKey: "",
      //   consumerSecret: "");

      //app.UseFacebookAuthentication(
      //   appId: "",
      //   appSecret: "");

      if (!string.IsNullOrWhiteSpace(ApiSecretsStorage.GoogleClientId) &&
          !string.IsNullOrWhiteSpace(ApiSecretsStorage.GoogleClientSecret))
      {
        var googleOAuth2AuthenticationOptions = new GoogleOAuth2AuthenticationOptions()
        {
          ClientId = ApiSecretsStorage.GoogleClientId,
          ClientSecret = ApiSecretsStorage.GoogleClientSecret,
        };
        googleOAuth2AuthenticationOptions.Scope.Add("email");
        app.UseGoogleAuthentication(googleOAuth2AuthenticationOptions);
      }

      if (!string.IsNullOrWhiteSpace(ApiSecretsStorage.VkClientId) &&
          !string.IsNullOrWhiteSpace(ApiSecretsStorage.VkClientSecret))
      {

        app.UseVkontakteAuthentication(new VkAuthenticationOptions
        {
          Scope = new List<string>() {"email"},
          ClientId = ApiSecretsStorage.VkClientId,
          ClientSecret = ApiSecretsStorage.VkClientSecret
        });
      }
    }
  }
}