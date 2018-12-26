﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Reflection.Emit;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Protocols;
using Tubumu.Modules.Admin.Entities;
using Tubumu.Modules.Admin.Models;
using Tubumu.Modules.Admin.Models.Api;
using Tubumu.Modules.Admin.Models.InputModels;
using Tubumu.Modules.Admin.Services;
using Tubumu.Modules.Framework.Authorization;
using Tubumu.Modules.Framework.Extensions;
using Tubumu.Modules.Framework.Models;
using Tubumu.Modules.Framework.Utilities.Security;
using Group = Tubumu.Modules.Admin.Models.Group;
using Permission = Tubumu.Modules.Admin.Models.Permission;

namespace Tubumu.Modules.Admin.Controllers
{
    public partial class AdminController : ControllerBase
    {

        #region Index

        [HttpGet("GetProfile")]
        public async Task<ApiItemResult> GetProfile()
        {
            var result = new ApiItemResult();
            if (!int.TryParse(HttpContext.User.Identity.Name, out var value))
            {
                result.Code = 400;
                result.Message = "获取用户失败: 认证失败";
                return result;
            }
            var userInfo = await _userService.GetItemByUserIdAsync(value, UserStatus.Normal);
            if(userInfo == null)
            {
                result.Code = 400;
                result.Message = "获取用户失败: 获取用户信息失败";
                return result;
            }
            var profile = new Profile
            {
                UserId = userInfo.UserId,
                Username = userInfo.Username,
                DisplayName = userInfo.DisplayName,
                HeadUrl = userInfo.HeadUrl,
                LogoUrl = userInfo.LogoUrl,
                Groups = await _groupService.GetInfoPathAsync(userInfo.Group.GroupId),
                Role = userInfo.Role,
            };

            result.Code = 200;
            result.Message = "获取用户成功";
            result.Item = profile;
            return result;
        }

        [HttpPost("ChangeProfile")]
        public async Task<ApiResult> ChangeProfile([FromBody]UserChangeProfileInput input)
        {
            var result = new ApiResult();
            if (!int.TryParse(HttpContext.User.Identity.Name, out var value))
            {
                result.Code = 400;
                result.Message = "修改资料失败: 获取用户信息失败";
            }
            var changeProfileResult = await _adminUserService.ChangeProfileAsync(value, input, ModelState);
            if (!changeProfileResult)
            {
                result.Code = 400;
                result.Message = "修改资料失败: " + ModelState.FirstErrorMessage();
            }
            else
            {
                result.Code = 200;
                result.Message = "修改资料成功";
            }

            return result;
        }

        [HttpPost("ChangePassword")]
        public async Task<ApiResult> ChangePassword([FromBody]UserChangePasswordInput input)
        {
            var result = new ApiResult();
            if (!int.TryParse(HttpContext.User.Identity.Name, out var value))
            {
                result.Code = 400;
                result.Message = "修改资料失败: 获取用户信息失败";
            }
           
            if (!await _adminUserService.ChangePasswordAsync(value, input, ModelState))
            {
                result.Code = 400;
                result.Message = "修改密码失败" + ModelState.FirstErrorMessage();
            }
            else
            {
                result.Code = 200;
                result.Message = "修改密码成功";
            }

            return result;
        }

        [HttpGet("GetMenus")]
        public ApiListResult GetMenus()
        {
            var list = new List<ModuleMenu>();
            var menuProviders = _menuProviders.OrderBy(m=>m.Order);
            foreach (var menuProvider in menuProviders)
            {
                var items = menuProvider.GetModuleMenus();
                foreach (var item in items)
                {
                    AddMenuToList(list, item);
                }
            }

            var result = new ApiListResult
            {
                Code = 200,
                Message = "获取菜单成功",
                List = list
            };
            return result;
        }

        #endregion

        #region Private Methods 

        private void AddMenuToList(List<ModuleMenu> list, ModuleMenu item)
        {
            if (!ValidateMenu(item)) return;

            if (item.Directly.HasValue && !item.Directly.Value)
            {
                // 避免无意义的序列化
                item.Directly = null;
            }

            if (item.Type == ModuleMenuType.Item && !item.Children.IsNullOrEmpty())
            {
                throw new Exception("菜单项【{0}】不能包含子项".FormatWith(item.Title));
            }
            if (item.Type == ModuleMenuType.Sub || item.Type == ModuleMenuType.Group)
            {
                if (!item.LinkRouteName.IsNullOrWhiteSpace())
                {
                    throw new Exception("{0}【{1}】不能设置路由".FormatWith(item.Type == ModuleMenuType.Sub ? "子菜单" : "菜单组", item.Title));
                }
                if (item.Directly.HasValue && item.Directly.Value)
                {
                    throw new Exception("{0}【{1}】不能设置为直接访问".FormatWith(item.Type == ModuleMenuType.Sub ? "子菜单" : "菜单组", item.Title));
                }

                if (item.Children.IsNullOrEmpty())
                {
                    // 如果类型是 Sub 或 Group 并且没有任何 Child，则本菜单也不用显示。
                    return;
                }

                var newChildren = new List<ModuleMenu>();
                item.Children.ForEach(m => AddMenuToList(newChildren, m));
                if (newChildren.IsNullOrEmpty())
                {
                    // 如果经过全选过滤，子菜单或菜单组已无子项，则本子菜单或菜单组也不用显示。
                    return;
                }
                item.Children = newChildren;
            }

            if (item.Type == ModuleMenuType.Item && ValidateMenu(item))
            {
                item.Link = Url.RouteUrl(item.LinkRouteName, item.LinkRouteValues);
            }
            list.Add(item);

        }

        private bool ValidateMenu(ModuleMenu item)
        {
            if (item.Permission.IsNullOrWhiteSpace() && item.Role.IsNullOrWhiteSpace() && item.Group.IsNullOrWhiteSpace() && item.Validator == null)
            {
                return true;
            }

            var user = HttpContext.User;
            if (item.Validator != null)
            {
                return item.Validator(user);
            }

            if (item.Permission != null)
            {
                var perArray = item.Permission.Split('|', ';', ',');
                foreach (var it in perArray)
                {
                    if (user.HasPermission(it))
                    {
                        return true;
                    }
                }
            }
            if (item.Role != null)
            {
                var rolArray = item.Role.Split('|', ';', ',');
                foreach (var it in rolArray)
                {
                    if (HttpContext.User.IsInRole(it))
                    {
                        return true;
                    }
                }
            }
            if (item.Group != null)
            {
                var grpArray = item.Group.Split('|', ';', ',');
                foreach (var it in grpArray)
                {
                    if (user.IsInGroup(it))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        #endregion

    }
}