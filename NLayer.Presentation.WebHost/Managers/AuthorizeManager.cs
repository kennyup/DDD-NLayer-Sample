﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Security;
using NLayer.Application.UserSystemModule.DTOs;
using NLayer.Application.UserSystemModule.Services;
using NLayer.Infrastructure.Authorize;
using NLayer.Infrastructure.Authorize.AuthObject;
using NLayer.Infrastructure.Helper;
using NLayer.Infrastructure.Utility.Caching;

namespace NLayer.Presentation.WebHost.Managers
{
    public class AuthorizeManager : IAuthorizeManager
    {
        private ICacheManager CacheManager { get; set; }

        private IAuthService AuthService { get; set; }

        public AuthorizeManager(ICacheManager cacheManager, IAuthService authService)
        {
            CacheManager = cacheManager;
            AuthService = authService;
        }

        #region Private Methods

        private static string GetTokenKey(string token)
        {
            return string.Format("NLAYER_AUTH_TOKEN_{0}", token);
        }

        private void RegisterToken(string token, string user = "NLayerUser", bool rememberMe = false)
        {
            if (token.IsNullOrBlank())
            {
                throw new ArgumentNullException("token", token);
            }

            DateTime expiration = rememberMe ? DateTime.Now.AddDays(7) : DateTime.Now.Add(FormsAuthentication.Timeout);

            var ticket = new FormsAuthenticationTicket(1, user, DateTime.Now, expiration, true, token, FormsAuthentication.FormsCookiePath);

            string hashTicket = FormsAuthentication.Encrypt(ticket);
            var userCookie = new HttpCookie(FormsAuthentication.FormsCookieName, hashTicket) { Domain = FormsAuthentication.CookieDomain };

            HttpContext.Current.Response.Cookies.Set(userCookie);
        }

        private void RemoveToken(string token)
        {
            CacheManager.Remove(GetTokenKey(token));
        }

        private string GetAuthToken(UserDTO user)
        {
            if (user == null)
            {
                throw new ArgumentNullException("user", @"user");
            }
            return string.Format("{0}_{1}", user.Id, user.LastLoginToken);
        }

        private void SetCacheAuthUser(string authToken, UserForAuthorize authUser)
        {
            // 缓存起来
            CacheManager.Set(GetTokenKey(authToken), authUser, 12 * 60);
        }

        private UserForAuthorize GetCurrentUserInfo(string token)
        {
            var authUser = CacheManager.Get<UserForAuthorize>(GetTokenKey(token));

            if (authUser == null)
            {
                // 尝试从数据库读取
                var datas = token.Split('_');
                if (datas.Length == 2)
                {
                    var userId = Guid.Parse(datas[0]);
                    var loginToken = datas[1];
                    var user = AuthService.FindByLoginToken(userId, loginToken);
                    if (user != null)
                    {
                        authUser = GetAuthUserFromDb(user);
                        // 写入到缓存

                    }
                }
            }

            if (authUser == null || !AuthService.ValidateLoginToken(authUser.UserId, token))
            {
                return null;
            }

            return authUser;
        }

        private UserForAuthorize GetAuthUserFromDb(UserDTO user)
        {
            var authUser = new UserForAuthorize()
            {
                LoginName = user.LoginName,
                UserId = user.Id,
                UserName = user.Name
            };

            // 权限和菜单信息
            var permissions = new List<PermissionForAuthDTO>();
            permissions.AddRange(AuthService.GetRolePermissions(user.Id));
            permissions.AddRange(AuthService.GetUserPermissions(user.Id));

            authUser.Permissions = permissions.Select(x =>
                new PermissionForAuthorize()
                {
                    PermissionId = x.PermissionId,
                    PermissionCode = x.PermissionCode,
                    MenuId = x.MenuId,
                    RoleId = x.RoleId,
                    FromUser = x.FromUser
                }).ToList();
            authUser.Menus = permissions.Select(x => new MenuForAuthorize()
            {
                MenuId = x.MenuId
            }).Distinct().ToList();

            return authUser;
        }
        #endregion

        public string GetCurrentTokenFromCookies()
        {
            if (HttpContext.Current.User.Identity.IsAuthenticated)
            {
                var token = ((FormsIdentity)HttpContext.Current.User.Identity).Ticket.UserData;
                if (token.NotNullOrBlank())
                {
                    return token;
                }
                throw new AuthorizeTokenNotFoundException();
            }
            throw new AuthorizeTokenNotFoundException();
        }

        public UserForAuthorize GetCurrentUserInfo()
        {
            var token = this.GetCurrentTokenFromCookies();

            return GetCurrentUserInfo(token);
        }

        public void SignIn(string loginName, string password, bool rememberMe = false)
        {
            var user = AuthService.Login(loginName, password, true);

            var authUser = GetAuthUserFromDb(user);

            var dataToken = GetAuthToken(user);

            // Cache
            SetCacheAuthUser(dataToken, authUser);

            // Cookies
            RegisterToken(dataToken, authUser.UserName, rememberMe);
        }

        public void SignOut()
        {
            try
            {
                var token = this.GetCurrentTokenFromCookies();

                RemoveToken(token);

                FormsAuthentication.SignOut();
            }
            catch (AuthorizeTokenNotFoundException)
            {
                return;
            }
        }

        public bool ValidatePermission(string permissionCode, bool throwExceptionIfNotPass = true)
        {
            var user = GetCurrentUserInfo();
            if (user == null)
            {
                throw new AuthorizeTokenInvalidException();
            }

            var permission = user.Permissions.FirstOrDefault(x => x.PermissionCode.EqualsIgnoreCase(permissionCode));

            if (permission == null && throwExceptionIfNotPass)
            {
                throw new AuthorizeNoPermissionException();
            }

            return permission != null;
        }
    }
}
