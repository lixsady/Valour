﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Valour.Server.Database;
using Valour.Server.Email;
using Valour.Server.Oauth;
using Valour.Server.Users;
using Valour.Server.Users.Identity;
using Valour.Shared;
using Valour.Shared.Oauth;
using Valour.Shared.Users;
using Valour.Shared.Users.Identity;

/*  Valour - A free and secure chat client
 *  Copyright (C) 2020 Vooper Media LLC
 *  This program is subject to the GNU Affero General Public license
 *  A copy of the license should be included - if not, see <http://www.gnu.org/licenses/>
 */

namespace Valour.Server.Controllers
{
    /// <summary>
    /// Provides routes for user-related functions on the server side.
    /// </summary>
    [ApiController]
    [Route("[controller]/[action]")]
    public class UserController
    {
        /// <summary>
        /// Database context
        /// </summary>
        private readonly ValourDB Context;
        private readonly UserManager UserManager;

        // Dependency injection
        public UserController(ValourDB context, UserManager userManager)
        {
            this.Context = context;
            this.UserManager = userManager;
        }

        /// <summary>
        /// Registers a new user and adds them to the database
        /// </summary>
        public async Task<TaskResult> RegisterUser(string username, string email, string password)
        {
            // Ensure unique username
            if (await Context.Users.AnyAsync(x => x.Username.ToLower() == username.ToLower()))
            {
                return new TaskResult(false, $"Failed: There was already a user named {username}");
            }

            // Ensure unique email
            if (await Context.Users.AnyAsync(x => x.Email.ToLower() == email.ToLower()))
            {
                return new TaskResult(false, $"Failed: There was already a user using the email {email}");
            }

            // Test password complexity
            TaskResult passwordResult = PasswordManager.TestComplexity(password);

            // Enforce password tests
            if (!passwordResult.Success)
            {
                return passwordResult;
            }

            // At this point the safety checks are complete

            // Generate random salt
            byte[] salt = new byte[32];
            PasswordManager.GenerateSalt(salt);

            // Generate password hash
            byte[] hash = PasswordManager.GetHashForPassword(password, salt);

            // Create user object
            User user = new User()
            {
                Username = username,
                Join_DateTime = DateTime.UtcNow,
                Email = email,
                Verified_Email = false
            };

            // An error here would be really bad so we'll be careful and catch any exceptions
            try
            {
                await Context.Users.AddAsync(user);
                await Context.SaveChangesAsync();
            }
            catch (System.Exception e)
            {
                return new TaskResult(false, $"A critical error occured adding the user.");
            }

            Credential cred = new Credential()
            {
                Credential_Type = CredentialType.PASSWORD,
                Identifier = email,
                Salt = salt,
                Secret = hash,
                User_Id = user.Id // We need to find what the user's assigned ID is (auto-filled by EF?)
            };

            // An error here would be really bad so we'll be careful and catch any exceptions
            try
            {
                await Context.Credentials.AddAsync(cred);
                await Context.SaveChangesAsync();
            }
            catch (System.Exception e)
            {
                return new TaskResult(false, $"A critical error occured adding the credentials.");
            }

            string code = Guid.NewGuid().ToString();

            EmailConfirmCode emailConfirm = new EmailConfirmCode()
            {
                Code = code,
                User_Id = user.Id
            };

            // An error here would be really bad so we'll be careful and catch any exceptions
            try
            {
                await Context.EmailConfirmCodes.AddAsync(emailConfirm);
                await Context.SaveChangesAsync();
            }
            catch (System.Exception e)
            {
                return new TaskResult(false, $"A critical error occured adding the email confirmation code.");
            }


            // Send registration email
            string emsg = $@"<body style='background-color:#040D14'>
                              <h2 style='font-family:Helvetica; color:white'>
                                Welcome to Valour!
                              </h2>
                              <p style='font-family:Helvetica; color:white'>
                                To verify your new account, please use this code as your password the first time you log in: 
                              </p>
                              <p style='font-family:Helvetica; color:#88ffff'>
                                {code}
                              </p>
                            </body>";

            string rawmsg = $"Welcome to Valour!\nTo verify your new account, please use this code as your password the first time you log in:\n{code}";

            await EmailManager.SendEmailAsync(email, "Valour Registration", rawmsg, emsg);

            return new TaskResult(true, $"Successfully created user {username}");
        }

        /// <summary>
        /// Registers a new bot and adds them to the database
        /// </summary>
        //public async Task<string> RegisterBot(string username, ulong owner_id, string password)
        //{
        //   TODO
        //}

        /// <summary>
        /// Allows for checking if a password meets standards though API
        /// </summary>
        public async Task<TaskResult> TestPasswordComplexity(string password)
        {
            // Regex can be slow, so we throw it in another thread
            return await Task.Run(() => PasswordManager.TestComplexity(password));
        }

        /// <summary>
        /// Allows a token to be requested using basic login information
        /// </summary>
        public async Task<TokenResponse> RequestStandardToken(string email, string password)
        {
            var result = await UserManager.ValidateAsync(CredentialType.PASSWORD, email, password);

            // If the verification failed, forward the failure
            if (!result.Result.Success)
            {
                return new TokenResponse(null, result.Result);
            }

            // Otherwise, get the user we just verified
            User user = result.User;

            if (!user.Verified_Email)
            {
                EmailConfirmCode confirmCode = await Context.EmailConfirmCodes.FindAsync(password);

                // Someone using another person's verification is a little
                // worrying, and we don't want them to know it worked, so we'll
                // send the same error either way.
                if (confirmCode == null || confirmCode.User_Id != user.Id)
                {
                    return new TokenResponse(null, new TaskResult(false, "The email associated with this account needs to be verified! Please log in using the code " +
                        "that was emailed as your password."));
                }

                // At this point the email has been confirmed
                user.Verified_Email = true;

                Context.EmailConfirmCodes.Remove(confirmCode);
                await Context.SaveChangesAsync();
            }

            // We now have to create a token for the user
            AuthToken token = new AuthToken()
            {
                App_Id = "VALOUR",
                Id = Guid.NewGuid().ToString(),
                Time = DateTime.UtcNow,
                Expires = DateTime.UtcNow.AddDays(7),
                Scope = Permission.FullControl.Value,
                User_Id = user.Id
            };

            using (ValourDB context = new ValourDB(ValourDB.DBOptions))
            {
                await context.AuthTokens.AddAsync(token);
                await context.SaveChangesAsync();
            }

            return new TokenResponse(token.Id, new TaskResult(true, "Successfully verified and retrieved token!"));
        }
    }
}
