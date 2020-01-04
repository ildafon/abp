﻿using Microsoft.Extensions.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Services;
using Volo.Abp.Identity.Localization;
using Volo.Abp.Threading;
using Volo.Abp.Uow;

namespace Volo.Abp.Identity.Organizations
{
    /// <summary>
    /// Performs domain logic for Organization Units.
    /// </summary>
    public class OrganizationUnitManager : DomainService
    {
        protected IOrganizationUnitRepository _organizationUnitRepository { get; private set; }

        private readonly IStringLocalizer<IdentityResource> _localizer;
        private readonly IIdentityRoleRepository _identityRoleRepository;
        private readonly ICancellationTokenProvider _cancellationTokenProvider;

        public OrganizationUnitManager(
            IOrganizationUnitRepository organizationUnitRepository,
            IStringLocalizer<IdentityResource> localizer,
            IIdentityRoleRepository identityRoleRepository,
            ICancellationTokenProvider cancellationTokenProvider)
        {
            _organizationUnitRepository = organizationUnitRepository;
            _localizer = localizer;
            _identityRoleRepository = identityRoleRepository;
            _cancellationTokenProvider = cancellationTokenProvider;
        }

        [UnitOfWork]
        public virtual async Task CreateAsync(OrganizationUnit organizationUnit)
        {
            organizationUnit.Code = await GetNextChildCodeAsync(organizationUnit.ParentId);
            await ValidateOrganizationUnitAsync(organizationUnit);
            await _organizationUnitRepository.InsertAsync(organizationUnit);
        }

        public virtual async Task UpdateAsync(OrganizationUnit organizationUnit)
        {
            await ValidateOrganizationUnitAsync(organizationUnit);
            await _organizationUnitRepository.UpdateAsync(organizationUnit);
        }

        public virtual async Task<string> GetNextChildCodeAsync(Guid? parentId)
        {
            var lastChild = await GetLastChildOrNullAsync(parentId);
            if (lastChild == null)
            {
                var parentCode = parentId != null ? await GetCodeOrDefaultAsync(parentId.Value) : null;
                return OrganizationUnit.AppendCode(parentCode, OrganizationUnit.CreateCode(1));
            }

            return OrganizationUnit.CalculateNextCode(lastChild.Code);
        }

        public virtual async Task<OrganizationUnit> GetLastChildOrNullAsync(Guid? parentId)
        {
            var children = await _organizationUnitRepository.GetChildrenAsync(parentId);
            return children.OrderBy(c => c.Code).LastOrDefault();
        }

        [UnitOfWork]
        public virtual async Task DeleteAsync(Guid id)
        {
            var children = await FindChildrenAsync(id, true);

            foreach (var child in children)
            {
                await _organizationUnitRepository.DeleteAsync(child);
            }

            await _organizationUnitRepository.DeleteAsync(id);
        }

        [UnitOfWork]
        public virtual async Task MoveAsync(Guid id, Guid? parentId)
        {
            var organizationUnit = await _organizationUnitRepository.GetAsync(id);
            if (organizationUnit.ParentId == parentId)
            {
                return;
            }

            //Should find children before Code change
            var children = await FindChildrenAsync(id, true);

            //Store old code of OU
            var oldCode = organizationUnit.Code;

            //Move OU
            organizationUnit.Code = await GetNextChildCodeAsync(parentId);
            organizationUnit.ParentId = parentId;

            await ValidateOrganizationUnitAsync(organizationUnit);

            //Update Children Codes
            foreach (var child in children)
            {
                child.Code = OrganizationUnit.AppendCode(organizationUnit.Code, OrganizationUnit.GetRelativeCode(child.Code, oldCode));
            }
        }

        public virtual async Task<string> GetCodeOrDefaultAsync(Guid id)
        {
            var ou = await _organizationUnitRepository.GetAsync(id);
            return ou?.Code;
        }

        protected virtual async Task ValidateOrganizationUnitAsync(OrganizationUnit organizationUnit)
        {
            var siblings = (await FindChildrenAsync(organizationUnit.ParentId))
                .Where(ou => ou.Id != organizationUnit.Id)
                .ToList();

            if (siblings.Any(ou => ou.DisplayName == organizationUnit.DisplayName))
            {
                throw new UserFriendlyException(_localizer["OrganizationUnitDuplicateDisplayNameWarning", organizationUnit.DisplayName]);
            }
        }

        public async Task<List<OrganizationUnit>> FindChildrenAsync(Guid? parentId, bool recursive = false)
        {
            if (!recursive)
            {
                return await _organizationUnitRepository.GetChildrenAsync(parentId);
            }

            if (!parentId.HasValue)
            {
                return await _organizationUnitRepository.GetListAsync();
            }

            var code = await GetCodeOrDefaultAsync(parentId.Value);

            return await _organizationUnitRepository.GetAllChildrenWithParentCodeAsync(code, parentId);
        }

        public virtual Task<bool> IsInOrganizationUnitAsync(IdentityUser user, OrganizationUnit ou)
        {
            return Task.FromResult(user.IsInOrganizationUnit(ou.Id));
        }

        public virtual async Task AddRoleToOrganizationUnitAsync(Guid roleId, Guid ouId)
        {
            await AddRoleToOrganizationUnitAsync(
                await _identityRoleRepository.GetAsync(roleId),
                await _organizationUnitRepository.GetAsync(ouId)
                );
        }

        public virtual Task AddRoleToOrganizationUnitAsync(IdentityRole role, OrganizationUnit ou)
        {
            var currentRoles = ou.Roles;

            if (currentRoles.Any(r => r.Id == role.Id))
            {
                return Task.FromResult(0);
            }
            ou.AddRole(role.Id);
            return Task.FromResult(0);
        }

        public virtual async Task RemoveRoleFromOrganizationUnitAsync(Guid roleId, Guid ouId)
        {
            await RemoveRoleFromOrganizationUnitAsync(
                await _identityRoleRepository.GetAsync(roleId),
                await _organizationUnitRepository.GetAsync(ouId, true)
                );
        }

        public virtual async Task RemoveRoleFromOrganizationUnitAsync(IdentityRole role, OrganizationUnit organizationUnit)
        {
            await _organizationUnitRepository.EnsureCollectionLoadedAsync(organizationUnit, ou => ou.Roles, _cancellationTokenProvider.Token).ConfigureAwait(false);

            organizationUnit.RemoveRole(role.Id);
        }
    }
}