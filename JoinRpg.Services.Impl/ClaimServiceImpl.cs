﻿using System;
using System.Collections.Generic;
using System.Data.Entity.Validation;
using System.Linq;
using System.Threading.Tasks;
using JetBrains.Annotations;
using JoinRpg.Data.Write.Interfaces;
using JoinRpg.DataModel;
using JoinRpg.Domain;
using JoinRpg.Helpers;
using JoinRpg.Services.Impl.ClaimProblemFilters;
using JoinRpg.Services.Interfaces;

namespace JoinRpg.Services.Impl
{
  [UsedImplicitly]
  public class ClaimServiceImpl : ClaimImplBase, IClaimService
  {
    

    public async Task AddClaimFromUser(int projectId, int? characterGroupId, int? characterId, int currentUserId, string claimText)
    {
      var source = await GetClaimSource(projectId, characterGroupId, characterId);

      EnsureCanAddClaim(currentUserId, source);

      var addClaimDate = DateTime.UtcNow;
      var responsibleMaster = source.GetResponsibleMasters().FirstOrDefault();
      var claim = new Claim()
      {
        CharacterGroupId = characterGroupId,
        CharacterId = characterId,
        ProjectId = projectId,
        PlayerUserId = currentUserId,
        PlayerAcceptedDate = addClaimDate,
        CreateDate =  addClaimDate,
        ClaimStatus = Claim.Status.AddedByUser,
        Comments = new List<Comment>()
        {
          new Comment()
          {
            AuthorUserId = currentUserId,
            CommentText = new MarkdownString(claimText),
            CreatedTime = addClaimDate,
            IsCommentByPlayer = true,
            IsVisibleToPlayer = true,
            ProjectId = projectId,
            LastEditTime = addClaimDate,
          }
        },
        ResponsibleMasterUserId = responsibleMaster?.UserId,
        ResponsibleMasterUser = responsibleMaster,
        Subscriptions = new List<UserSubscription>(), //We need this as we are using it later
        LastUpdateDateTime = addClaimDate
      };
      UnitOfWork.GetDbSet<Claim>().Add(claim);
      await UnitOfWork.SaveChangesAsync();

      var email = await CreateClaimEmail<NewClaimEmail>(claim, currentUserId, claimText, s => s.ClaimStatusChange);
      await EmailService.Email(email);
    }

    private async Task<IClaimSource> GetClaimSource(int projectId, int? characterGroupId, int? characterId)
    {
      var characterGroup = characterGroupId != null
        ? await ProjectRepository.LoadGroupAsync(projectId, characterGroupId.Value)
        : null;
      var character = characterId != null ? await ProjectRepository.GetCharacterAsync(projectId, characterId.Value) : null;

      var source = new IClaimSource[] {characterGroup, character}.WhereNotNull().Single();
      if (!source.IsAvailable)
      {
        throw new DbEntityValidationException();
      }
      return source;
    }

    private static void EnsureCanAddClaim<T>(int currentUserId, T claimSource) where T: IClaimSource
    {
      //TODO add more validation checks, move to Domain
      if (claimSource.HasClaimForUser(currentUserId))
      {
        throw new DbEntityValidationException();
      }
      if (!claimSource.IsAvailable)
      {
        throw new DbEntityValidationException();
      }
    }

    public async Task AddComment(int projectId, int claimId, int currentUserId, int? parentCommentId, bool isVisibleToPlayer, string commentText, FinanceOperationAction financeAction)
    {
      var claim = await LoadClaim(projectId, claimId, currentUserId);
      var now = DateTime.UtcNow;

      var parentComment = claim.Comments.SingleOrDefault(c => c.CommentId == parentCommentId);

      if (financeAction != FinanceOperationAction.None)
      {
        var finance = parentComment?.Finance;
        if (finance == null)
        {
          throw new InvalidOperationException();
        }

        if (!finance.RequireModeration)
        {
          throw new ValueAlreadySetException("Finance entry is already moderated.");
        }

        finance.RequestModerationAccess(currentUserId);
        finance.Changed = now;
        switch (financeAction)
        {
          case FinanceOperationAction.Approve:
            finance.State = FinanceOperationState.Approved;
            break;
          case FinanceOperationAction.Decline:
          finance.State = FinanceOperationState.Declined;
          break;
          default:
            throw new ArgumentOutOfRangeException(nameof(financeAction), financeAction, null);
        }
      }

      
      claim.AddCommentImpl(currentUserId, parentCommentId, commentText, now, isVisibleToPlayer);

      await UnitOfWork.SaveChangesAsync();

      var addCommentEmail = await CreateClaimEmail<AddCommentEmail>(claim, currentUserId, commentText, s => s.Comments);
      addCommentEmail.Recepients.Add(claim.Comments.SingleOrDefault(c => c.CommentId == parentCommentId)?.Author);
      await EmailService.Email(addCommentEmail);
    }

    public async Task AppoveByMaster(int projectId, int claimId, int currentUserId, string commentText)
    {
      var claim = await LoadClaimForApprovalDecline(projectId, claimId, currentUserId);
      var now = DateTime.UtcNow;

      claim.EnsureStatus(Claim.Status.AddedByUser);
      claim.MasterAcceptedDate = now;
      claim.ClaimStatus = Claim.Status.Approved;

      claim.ResponsibleMasterUserId = claim.ResponsibleMasterUserId ?? currentUserId;
      claim.AddCommentImpl(currentUserId, null, commentText, now, true);

      foreach (var otherClaim in claim.OtherActiveClaimsForThisPlayer())
      {
        claim.EnsureStatus(Claim.Status.AddedByUser, Claim.Status.AddedByMaster);
        otherClaim.MasterDeclinedDate = now;
        otherClaim.ClaimStatus = Claim.Status.DeclinedByMaster;
        await EmailService.Email(await AddCommentWithEmail<DeclineByMasterEmail>(currentUserId, "Заявка автоматически отклонена, т.к. другая заявка того же игрока была принята в тот же проект", otherClaim, now, true, s => s.ClaimStatusChange));
      }

      if (claim.Group != null)
      {
        //TODO: Добавить здесь возможность ввести имя персонажа или брать его из заявки
        claim.ConvertToIndividual();
      }

      await EmailService.Email(await CreateClaimEmail<ApproveByMasterEmail>(claim, currentUserId, commentText, s => s.ClaimStatusChange));
      await UnitOfWork.SaveChangesAsync();
    }

    public async Task DeclineByMaster(int projectId, int claimId, int currentUserId, string commentText)
    {
      var claim = await LoadClaimForApprovalDecline(projectId, claimId, currentUserId);
      claim.EnsureStatus(Claim.Status.AddedByUser, Claim.Status.AddedByMaster, Claim.Status.Approved);

      DateTime now = DateTime.UtcNow;

      claim.MasterDeclinedDate = now;
      claim.ClaimStatus = Claim.Status.DeclinedByMaster;
      var email = await AddCommentWithEmail<DeclineByMasterEmail>(currentUserId, commentText, claim, now, true, s => s.ClaimStatusChange);

      await UnitOfWork.SaveChangesAsync();
      await EmailService.Email(email);
    }

    public async Task RestoreByMaster(int projectId, int claimId, int currentUserId, string commentText)
    {
      var claim = await LoadClaimForApprovalDecline(projectId, claimId, currentUserId);
      var now = DateTime.UtcNow;

      claim.EnsureStatus(Claim.Status.DeclinedByUser, Claim.Status.DeclinedByMaster);

      claim.ClaimStatus = Claim.Status.AddedByUser; //TODO: Actually should be "AddedByMaster" but we don't support it yet.

      claim.ResponsibleMasterUserId = claim.ResponsibleMasterUserId ?? currentUserId;

      if (claim.CharacterId != null && claim.OtherClaimsForThisCharacter().Any(c => c.IsApproved))
      {
        claim.CharacterId = null;
        claim.CharacterGroupId = claim.Project.RootGroup.CharacterGroupId;
      }
      
      var email = await AddCommentWithEmail<RestoreByMasterEmail>(currentUserId, commentText, claim, now, true, s => s.ClaimStatusChange);

      await UnitOfWork.SaveChangesAsync();
      await EmailService.Email(email);
    }

    public async Task MoveByMaster(int projectId, int claimId, int currentUserId, string contents, int? characterGroupId, int? characterId)
    {
      var claim = await LoadClaimForApprovalDecline(projectId, claimId, currentUserId);
      await GetClaimSource(projectId, characterGroupId, characterId);

      claim.CharacterGroupId = characterGroupId;
      claim.CharacterId = characterId;

      if (claim.IsApproved && claim.CharacterId == null)
      {
        throw new DbEntityValidationException();
      }

      claim.ResponsibleMasterUserId = claim.ResponsibleMasterUserId ?? currentUserId;
      var email = await AddCommentWithEmail<MoveByMasterEmail>(currentUserId, contents, claim, DateTime.UtcNow, isVisibleToPlayer: true, predicate: s => s.ClaimStatusChange);

      await UnitOfWork.SaveChangesAsync();
      await EmailService.Email(email);
    }


    public async Task DeclineByPlayer(int projectId, int claimId, int currentUserId, string commentText)
    {
      var claim = await LoadMyClaim(projectId, claimId, currentUserId);
      claim.EnsureStatus(Claim.Status.AddedByUser, Claim.Status.AddedByMaster, Claim.Status.Approved);

      DateTime now = DateTime.UtcNow;
      claim.PlayerDeclinedDate = now;
      claim.ClaimStatus = Claim.Status.DeclinedByUser;

      var email = await AddCommentWithEmail<DeclineByPlayerEmail>(currentUserId, commentText, claim, now, true, s => s.ClaimStatusChange);

      await UnitOfWork.SaveChangesAsync();
      await EmailService.Email(email);
    }

    private async Task<T> AddCommentWithEmail<T>(int currentUserId, string commentText, Claim claim, DateTime now, bool isVisibleToPlayer, Func<UserSubscription, bool> predicate) where T : ClaimEmailModel, new()
    {
      claim.AddCommentImpl(currentUserId, null, commentText, now, isVisibleToPlayer);
      return await CreateClaimEmail<T>(claim, currentUserId, commentText, predicate);
    }

    public async Task SetResponsible(int projectId, int claimId, int currentUserId, int responsibleMasterId)
    {
      var claim = await ProjectRepository.GetClaim(projectId, claimId);
      claim.RequestMasterAccess(currentUserId);
      claim.RequestMasterAccess(responsibleMasterId);


      var newMaster = await UserRepository.GetById(responsibleMasterId);
      claim.ResponsibleMasterUserId = responsibleMasterId;

      claim.AddCommentImpl(currentUserId, null, $"Отвественный мастер: {claim.ResponsibleMasterUser?.DisplayName ?? "нет"} → {newMaster.DisplayName}", DateTime.UtcNow, isVisibleToPlayer: false);
      await UnitOfWork.SaveChangesAsync();
    }

    public async Task<IList<ClaimProblem>> GetProblemClaims(int projectId)
    {
      var project = await ClaimsRepository.GetClaims(projectId);
      var filters = new IClaimProblemFilter[]
      {
        new ResponsibleMasterProblemFilter(), new NotAnsweredClaim(), new ApprovedAndOtherClaimProblemFilter(),
        new FinanceProblemsFilter(),
      };
      return project.Claims.Where(claim => claim.IsActive).SelectMany(claim => filters.SelectMany(f => f.GetProblems(project, claim))).ToList();
    }

    private async Task<Claim> LoadClaimForApprovalDecline(int projectId, int claimId, int currentUserId)
    {
      var claim = await ProjectRepository.GetClaim(projectId, claimId);
      claim.RequestMasterAccess(currentUserId, acl => acl.CanApproveClaims);
      return claim;
    }

    private async Task<Claim> LoadMyClaim(int projectId, int claimId, int currentUserId)
    {
      var claim = await LoadClaim(projectId, claimId, currentUserId);
      if (claim.PlayerUserId != currentUserId)
      {
        throw new DbEntityValidationException();
      }
      return claim;
    }

    public ClaimServiceImpl(IUnitOfWork unitOfWork, IEmailService emailService) : base(unitOfWork, emailService)
    {
    }
  }

  internal static class ClaimStaticExtensions
  {
    public static Comment AddCommentImpl(this Claim claim, int currentUserId, int? parentCommentId, string commentText, DateTime now, bool isVisibleToPlayer)
    {
      if (!isVisibleToPlayer && claim.PlayerUserId == currentUserId)
      {
        throw new DbEntityValidationException();
      }

      var comment = new Comment()
      {
        ProjectId = claim.ProjectId, AuthorUserId = currentUserId, ClaimId = claim.ClaimId, CommentText = new MarkdownString(commentText), CreatedTime = now, IsCommentByPlayer = claim.PlayerUserId == currentUserId, IsVisibleToPlayer = isVisibleToPlayer, ParentCommentId = parentCommentId, LastEditTime = now
      };
      claim.Comments.Add(comment);

      claim.LastUpdateDateTime = now;

      return comment;
    }

    public static void ConvertToIndividual(this Claim claim)
    {
      if (claim.Group.AvaiableDirectSlots == 0)
      {
        throw new DbEntityValidationException();
      }
      if (claim.Group.AvaiableDirectSlots > 0)
      {
        claim.Group.AvaiableDirectSlots -= 1;
      }
      var character = new Character()
      {
        CharacterName = $"Новый персонаж в группе {claim.Group.CharacterGroupName}", ProjectId = claim.ProjectId, IsAcceptingClaims = true, IsPublic = claim.Group.IsPublic, Groups = new List<CharacterGroup>()
        {
          claim.Group
        }
      };
      claim.CharacterGroupId = null;
      claim.Character = character;
    }

    public static IEnumerable<User> GetSubscriptions(this Claim claim, Func<UserSubscription, bool> predicate, int initiatorUserId)
    {
      return claim.GetParentGroups() //Get all groups for claim
        .SelectMany(g => g.Subscriptions) //get subscriptions on groups
        .Union(claim.Subscriptions) //subscribtions on claim
        .Union(claim.Character?.Subscriptions ?? new UserSubscription[] {}) //and on characters
        .Where(predicate) //type of subscribe (on new comments, on new claims etc.)
        .Select(u => u.User) //Select users
        .Union(claim.ResponsibleMasterUser) //Responsible master is always subscribed on everything
        .Union(claim.Player) //...and player himself also
        .Where(u => u != null && u.UserId != initiatorUserId) //Do not send mail to self (and also will remove nulls)
        .Distinct() //One user can be subscribed by multiple reasons
        ;
    }
  }
}
