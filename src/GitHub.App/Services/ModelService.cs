﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using Akavache;
using GitHub.Api;
using GitHub.Caches;
using GitHub.Collections;
using GitHub.Extensions;
using GitHub.Extensions.Reactive;
using GitHub.Models;
using GitHub.Primitives;
using NullGuard;
using Octokit;
using Serilog;

namespace GitHub.Services
{
    [Export(typeof(IModelService))]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    public class ModelService : IModelService
    {
        static readonly ILogger log = Log.ForContext<ModelService>();

        public const string PRPrefix = "pr";
        readonly IApiClient apiClient;
        readonly IBlobCache hostCache;
        readonly IAvatarProvider avatarProvider;

        public ModelService(IApiClient apiClient, IBlobCache hostCache, IAvatarProvider avatarProvider)
        {
            this.apiClient = apiClient;
            this.hostCache = hostCache;
            this.avatarProvider = avatarProvider;
        }

        public IObservable<GitIgnoreItem> GetGitIgnoreTemplates()
        {
            return Observable.Defer(() =>
                hostCache.GetAndFetchLatestFromIndex(CacheIndex.GitIgnoresPrefix, () =>
                        GetGitIgnoreTemplatesFromApi(),
                        item => { },
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromDays(7))
                )
                .Select(Create)
                .Catch<GitIgnoreItem, Exception>(e =>
                {
                    log.Information(e, "Failed to retrieve GitIgnoreTemplates");
                    return Observable.Empty<GitIgnoreItem>();
                });
        }

        public IObservable<LicenseItem> GetLicenses()
        {
            return Observable.Defer(() =>
                hostCache.GetAndFetchLatestFromIndex(CacheIndex.LicensesPrefix, () =>
                        GetLicensesFromApi(),
                        item => { },
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromDays(7))
                )
                .Select(Create)
                .Catch<LicenseItem, Exception>(e =>
                {
                    log.Information(e, "Failed to retrieve licenses");
                    return Observable.Empty<LicenseItem>();
                });
        }

        public IObservable<IReadOnlyList<IAccount>> GetAccounts()
        {
            return Observable.Zip(
                GetUser(),
                GetUserOrganizations(),
                (user, orgs) => user.Concat(orgs))
            .ToReadOnlyList(Create);
        }

        IObservable<LicenseCacheItem> GetLicensesFromApi()
        {
            return apiClient.GetLicenses()
                .WhereNotNull()
                .Select(LicenseCacheItem.Create);
        }

        IObservable<GitIgnoreCacheItem> GetGitIgnoreTemplatesFromApi()
        {
            return apiClient.GetGitIgnoreTemplates()
                .WhereNotNull()
                .Select(GitIgnoreCacheItem.Create);
        }

        IObservable<IEnumerable<AccountCacheItem>> GetUser()
        {
            return hostCache.GetAndRefreshObject("user",
                () => apiClient.GetUser().Select(AccountCacheItem.Create), TimeSpan.FromMinutes(5), TimeSpan.FromDays(7))
                .TakeLast(1)
                .ToList();
        }

        IObservable<IEnumerable<AccountCacheItem>> GetUserOrganizations()
        {
            return GetUserFromCache().SelectMany(user =>
                hostCache.GetAndRefreshObject(user.Login + "|orgs",
                    () => apiClient.GetOrganizations().Select(AccountCacheItem.Create).ToList(),
                    TimeSpan.FromMinutes(2), TimeSpan.FromDays(7)))
                // TODO: Akavache returns the cached version followed by the fresh version if > 2
                // minutes have expired from the last request. Here we make sure the latest value is
                // returned but it's a hack. We really need a better way to cache this stuff.
                .TakeLast(1)
                .Catch<IEnumerable<AccountCacheItem>, KeyNotFoundException>(
                    // This could in theory happen if we try to call this before the user is logged in.
                    e =>
                    {
                        log.Error(e, "Retrieve user organizations failed because user is not stored in the cache.");
                        return Observable.Return(Enumerable.Empty<AccountCacheItem>());
                    })
                 .Catch<IEnumerable<AccountCacheItem>, Exception>(e =>
                 {
                     log.Error(e, "Retrieve user organizations failed.");
                     return Observable.Return(Enumerable.Empty<AccountCacheItem>());
                 });
        }

        public IObservable<IReadOnlyList<IRemoteRepositoryModel>> GetRepositories()
        {
            return GetUserRepositories(RepositoryType.Owner)
                .TakeLast(1)
                .Concat(GetUserRepositories(RepositoryType.Member).TakeLast(1))
                .Concat(GetAllRepositoriesForAllOrganizations());
        }

        public IObservable<AccountCacheItem> GetUserFromCache()
        {
            return Observable.Defer(() => hostCache.GetObject<AccountCacheItem>("user"));
        }

        /// <summary>
        /// Gets a collection of Pull Requests. If you want to refresh existing data, pass a collection in
        /// </summary>
        /// <param name="repo"></param>
        /// <param name="collection"></param>
        /// <returns></returns>
        public ITrackingCollection<IPullRequestModel> GetPullRequests(ILocalRepositoryModel repo,
            ITrackingCollection<IPullRequestModel> collection)
        {
            // Since the api to list pull requests returns all the data for each pr, cache each pr in its own entry
            // and also cache an index that contains all the keys for each pr. This way we can fetch prs in bulk
            // but also individually without duplicating information. We store things in a custom observable collection
            // that checks whether an item is being updated (coming from the live stream after being retrieved from cache)
            // and replaces it instead of appending, so items get refreshed in-place as they come in.

            var keyobs = GetUserFromCache()
                .Select(user => string.Format(CultureInfo.InvariantCulture, "{0}|{1}:{2}", CacheIndex.PRPrefix, user.Login, repo.Name));

            var source = Observable.Defer(() => keyobs
                .SelectMany(key =>
                    hostCache.GetAndFetchLatestFromIndex(key, () =>
                        apiClient.GetPullRequestsForRepository(repo.CloneUrl.Owner, repo.CloneUrl.RepositoryName)
                                 .Select(PullRequestCacheItem.Create),
                        item =>
                        {
                            // this could blow up due to the collection being disposed somewhere else
                            try { collection.RemoveItem(Create(item)); }
                            catch (ObjectDisposedException) { }
                        },
                        TimeSpan.Zero,
                        TimeSpan.FromDays(7))
                )
                .Select(Create)
            );

            collection.Listen(source);
            return collection;
        }

        public IObservable<IPullRequestModel> GetPullRequest(ILocalRepositoryModel repo, int number)
        {
            return Observable.Defer(() =>
            {
                return hostCache.GetAndRefreshObject(PRPrefix + '|' + number, () =>
                        Observable.CombineLatest(
                            apiClient.GetPullRequest(repo.CloneUrl.Owner, repo.CloneUrl.RepositoryName, number),
                            apiClient.GetPullRequestFiles(repo.CloneUrl.Owner, repo.CloneUrl.RepositoryName, number).ToList(),
                            (pr, files) => new { PullRequest = pr, Files = files })
                            .Select(x => PullRequestCacheItem.Create(x.PullRequest, (IReadOnlyList<PullRequestFile>)x.Files)),
                        TimeSpan.Zero,
                        TimeSpan.FromDays(7))
                    .Select(Create);
            });
        }

        public ITrackingCollection<IRemoteRepositoryModel> GetRepositories(ITrackingCollection<IRemoteRepositoryModel> collection)
        {
            var keyobs = GetUserFromCache()
                .Select(user => string.Format(CultureInfo.InvariantCulture, "{0}|{1}", CacheIndex.RepoPrefix, user.Login));

            var source = Observable.Defer(() => keyobs
                .SelectMany(key =>
                    hostCache.GetAndFetchLatestFromIndex(key, () =>
                        apiClient.GetRepositories()
                                 .Select(RepositoryCacheItem.Create),
                        item =>
                        {
                            // this could blow up due to the collection being disposed somewhere else
                            try { collection.RemoveItem(Create(item)); }
                            catch (ObjectDisposedException) { }
                        },
                        TimeSpan.FromMinutes(5),
                        TimeSpan.FromDays(1))
                )
                .Select(Create)
            );

            collection.Listen(source);
            return collection;
        }

        public IObservable<IPullRequestModel> CreatePullRequest(ILocalRepositoryModel sourceRepository, IRepositoryModel targetRepository,
            IBranch sourceBranch, IBranch targetBranch,
            string title, string body)
        {
            var keyobs = GetUserFromCache()
                .Select(user => string.Format(CultureInfo.InvariantCulture, "{0}|{1}:{2}", CacheIndex.PRPrefix, targetRepository.Owner, targetRepository.Name));

            return Observable.Defer(() => keyobs
                .SelectMany(key =>
                    hostCache.PutAndUpdateIndex(key, () =>
                        apiClient.CreatePullRequest(
                                new NewPullRequest(title,
                                                   string.Format(CultureInfo.InvariantCulture, "{0}:{1}", sourceRepository.Owner, sourceBranch.Name),
                                                   targetBranch.Name)
                                                   { Body = body },
                                targetRepository.Owner,
                                targetRepository.Name)
                            .Select(PullRequestCacheItem.Create)
                        ,
                        TimeSpan.FromMinutes(30))
                )
                .Select(Create)
            );
        }

        public IObservable<Unit> InvalidateAll()
        {
            return hostCache.InvalidateAll().ContinueAfter(() => hostCache.Vacuum());
        }

        IObservable<IReadOnlyList<IRemoteRepositoryModel>> GetUserRepositories(RepositoryType repositoryType)
        {
            return Observable.Defer(() => GetUserFromCache().SelectMany(user =>
                hostCache.GetAndRefreshObject(string.Format(CultureInfo.InvariantCulture, "{0}|{1}:repos", user.Login, repositoryType),
                    () => GetUserRepositoriesFromApi(repositoryType),
                        TimeSpan.FromMinutes(2),
                        TimeSpan.FromDays(7)))
                .ToReadOnlyList(Create))
                .Catch<IReadOnlyList<IRemoteRepositoryModel>, KeyNotFoundException>(
                    // This could in theory happen if we try to call this before the user is logged in.
                    e =>
                    {
                        log.Error(e,
                            "Retrieving {repositoryType} user repositories failed because user is not stored in the cache.",
                            repositoryType);
                        return Observable.Return(new IRemoteRepositoryModel[] {});
                    });
        }

        IObservable<IEnumerable<RepositoryCacheItem>> GetUserRepositoriesFromApi(RepositoryType repositoryType)
        {
            return apiClient.GetUserRepositories(repositoryType)
                .WhereNotNull()
                .Select(RepositoryCacheItem.Create)
                .ToList()
                .Catch<IEnumerable<RepositoryCacheItem>, Exception>(_ => Observable.Return(Enumerable.Empty<RepositoryCacheItem>()));
        }

        IObservable<IReadOnlyList<IRemoteRepositoryModel>> GetAllRepositoriesForAllOrganizations()
        {
            return GetUserOrganizations()
                .SelectMany(org => org.ToObservable())
                .SelectMany(org => GetOrganizationRepositories(org.Login).TakeLast(1));
        }

        IObservable<IReadOnlyList<IRemoteRepositoryModel>> GetOrganizationRepositories(string organization)
        {
            return Observable.Defer(() => GetUserFromCache().SelectMany(user =>
                hostCache.GetAndRefreshObject(string.Format(CultureInfo.InvariantCulture, "{0}|{1}|repos", user.Login, organization),
                    () => apiClient.GetRepositoriesForOrganization(organization).Select(
                        RepositoryCacheItem.Create).ToList(),
                        TimeSpan.FromMinutes(2),
                        TimeSpan.FromDays(7)))
                .ToReadOnlyList(Create))
                .Catch<IReadOnlyList<IRemoteRepositoryModel>, KeyNotFoundException>(
                    // This could in theory happen if we try to call this before the user is logged in.
                    e =>
                    {
                        log.Error(e, "Retrieveing {organization} org repositories failed because user is not stored in the cache.",
                            organization);
                        return Observable.Return(new IRemoteRepositoryModel[] {});
                    });
        }

        public IObservable<IBranch> GetBranches(IRepositoryModel repo)
        {
            var keyobs = GetUserFromCache()
                .Select(user => string.Format(CultureInfo.InvariantCulture, "{0}|{1}|branch", user.Login, repo.Name));

            return Observable.Defer(() => keyobs
                    .SelectMany(key => apiClient.GetBranches(repo.CloneUrl.Owner, repo.CloneUrl.RepositoryName)))
                .Select(x => new BranchModel(x, repo));
        }

        static GitIgnoreItem Create(GitIgnoreCacheItem item)
        {
            return GitIgnoreItem.Create(item.Name);
        }

        static LicenseItem Create(LicenseCacheItem licenseCacheItem)
        {
            return new LicenseItem(licenseCacheItem.Key, licenseCacheItem.Name);
        }

        IAccount Create(AccountCacheItem accountCacheItem)
        {
            return new Models.Account(
                accountCacheItem.Login,
                accountCacheItem.IsUser,
                accountCacheItem.IsEnterprise,
                accountCacheItem.OwnedPrivateRepositoriesCount,
                accountCacheItem.PrivateRepositoriesInPlanCount,
                avatarProvider.GetAvatar(accountCacheItem));
        }

        IRemoteRepositoryModel Create(RepositoryCacheItem item)
        {
            return new RemoteRepositoryModel(
                item.Id,
                item.Name,
                new UriString(item.CloneUrl),
                item.Private,
                item.Fork,
                Create(item.Owner))
            {
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt
            };
        }

        private GitReferenceModel Create(GitReferenceCacheItem item)
        {
            return new GitReferenceModel(item.Ref, item.Label, item.Sha, item.RepositoryCloneUrl);
        }

        IPullRequestModel Create(PullRequestCacheItem prCacheItem)
        {
            return new PullRequestModel(
                prCacheItem.Number,
                prCacheItem.Title,
                Create(prCacheItem.Author),
                prCacheItem.CreatedAt,
                prCacheItem.UpdatedAt)
            {
                Assignee = prCacheItem.Assignee != null ? Create(prCacheItem.Assignee) : null,
                Base = Create(prCacheItem.Base),
                Body = prCacheItem.Body ?? string.Empty,
                ChangedFiles = prCacheItem.ChangedFiles.Select(x => (IPullRequestFileModel)new PullRequestFileModel(x.FileName, x.Status)).ToList(),
                CommentCount = prCacheItem.CommentCount,
                CommitCount = prCacheItem.CommitCount,
                CreatedAt = prCacheItem.CreatedAt,
                Head = Create(prCacheItem.Head),
                State = prCacheItem.State.HasValue ? 
                    prCacheItem.State.Value : 
                    prCacheItem.IsOpen.Value ? PullRequestStateEnum.Open : PullRequestStateEnum.Closed,                
            };
        }

        public IObservable<Unit> InsertUser(AccountCacheItem user)
        {
            return hostCache.InsertObject("user", user);
        }

        protected virtual void Dispose(bool disposing)
        {}

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public class GitIgnoreCacheItem : CacheItem
        {
            public static GitIgnoreCacheItem Create(string ignore)
            {
                return new GitIgnoreCacheItem { Key = ignore, Name = ignore, Timestamp = DateTime.Now };
            }

            public string Name { get; set; }
        }


        public class LicenseCacheItem : CacheItem
        {
            public static LicenseCacheItem Create(LicenseMetadata licenseMetadata)
            {
                return new LicenseCacheItem { Key = licenseMetadata.Key, Name = licenseMetadata.Name, Timestamp = DateTime.Now };
            }

            public string Name { get; set; }
        }

        public class RepositoryCacheItem : CacheItem
        {
            public static RepositoryCacheItem Create(Repository apiRepository)
            {
                return new RepositoryCacheItem(apiRepository);
            }

            public RepositoryCacheItem() {}

            public RepositoryCacheItem(Repository apiRepository)
            {
                Id = apiRepository.Id;
                Name = apiRepository.Name;
                Owner = AccountCacheItem.Create(apiRepository.Owner);
                CloneUrl = apiRepository.CloneUrl;
                Private = apiRepository.Private;
                Fork = apiRepository.Fork;
                Key = string.Format(CultureInfo.InvariantCulture, "{0}/{1}", Owner.Login, Name);
                CreatedAt = apiRepository.CreatedAt;
                UpdatedAt = apiRepository.UpdatedAt;
                Timestamp = apiRepository.UpdatedAt;
            }

            public long Id { get; set; }

            public string Name { get; set; }
            [AllowNull]
            public AccountCacheItem Owner
            {
                [return: AllowNull]
                get; set;
            }
            public string CloneUrl { get; set; }
            public bool Private { get; set; }
            public bool Fork { get; set; }
            public DateTimeOffset CreatedAt { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
        }

        [NullGuard(ValidationFlags.None)]
        public class PullRequestCacheItem : CacheItem
        {
            public static PullRequestCacheItem Create(PullRequest pr)
            {
                return new PullRequestCacheItem(pr, new PullRequestFile[0]);
            }

            public static PullRequestCacheItem Create(PullRequest pr, IReadOnlyList<PullRequestFile> files)
            {
                return new PullRequestCacheItem(pr, files);
            }

            public PullRequestCacheItem() {}

            public PullRequestCacheItem(PullRequest pr)
                : this(pr, new PullRequestFile[0])
            {
            }

            public PullRequestCacheItem(PullRequest pr, IReadOnlyList<PullRequestFile> files)
            {
                Title = pr.Title;
                Number = pr.Number;
                Base = new GitReferenceCacheItem
                {
                    Label = pr.Base.Label,
                    Ref = pr.Base.Ref,
                    Sha = pr.Base.Sha,
                    RepositoryCloneUrl = pr.Base.Repository.CloneUrl,
                };
                Head = new GitReferenceCacheItem
                {
                    Label = pr.Head.Label,
                    Ref = pr.Head.Ref,
                    Sha = pr.Head.Sha,
                    RepositoryCloneUrl = pr.Head.Repository?.CloneUrl
                };
                CommentCount = pr.Comments + pr.ReviewComments;
                CommitCount = pr.Commits;
                Author = new AccountCacheItem(pr.User);
                Assignee = pr.Assignee != null ? new AccountCacheItem(pr.Assignee) : null;
                CreatedAt = pr.CreatedAt;
                UpdatedAt = pr.UpdatedAt;
                Body = pr.Body;
                ChangedFiles = files.Select(x => new PullRequestFileCacheItem(x)).ToList();
                State = GetState(pr);
                IsOpen = pr.State == ItemState.Open;
                Merged = pr.Merged;
                Key = Number.ToString(CultureInfo.InvariantCulture);
                Timestamp = UpdatedAt;
            }

            public string Title {get; set; }
            public int Number { get; set; }
            public GitReferenceCacheItem Base { get; set; }
            public GitReferenceCacheItem Head { get; set; }
            public int CommentCount { get; set; }
            public int CommitCount { get; set; }
            public AccountCacheItem Author { get; set; }
            public AccountCacheItem Assignee { get; set; }
            public DateTimeOffset CreatedAt { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
            public string Body { get; set; }
            public IList<PullRequestFileCacheItem> ChangedFiles { get; set; } = new PullRequestFileCacheItem[0];

            // Nullable for compatibility with old caches.
            public PullRequestStateEnum? State { get; set; }

            // This fields exists only for compatibility with old caches. The State property should be used.
            public bool? IsOpen { get; set; }
            public bool? Merged { get; set; }

            static PullRequestStateEnum GetState(PullRequest pullRequest)
            {
                if (pullRequest.State == ItemState.Open)
                {
                    return PullRequestStateEnum.Open;
                }
                else if (pullRequest.Merged)
                {
                    return PullRequestStateEnum.Merged;
                }
                else
                {
                    return PullRequestStateEnum.Closed;
                }
            }
        }

        [NullGuard(ValidationFlags.None)]
        public class PullRequestFileCacheItem
        {
            public PullRequestFileCacheItem()
            {
            }

            public PullRequestFileCacheItem(PullRequestFile file)
            {
                FileName = file.FileName;
                Status = (PullRequestFileStatus)Enum.Parse(typeof(PullRequestFileStatus), file.Status, true);
            }

            public string FileName { get; set; }
            public PullRequestFileStatus Status { get; set; }
        }

        [NullGuard(ValidationFlags.None)]
        public class GitReferenceCacheItem
        {
            public string Ref { get; set; }
            public string Label { get; set; }
            public string Sha { get; set; }
            public string RepositoryCloneUrl { get; set; }
        }
    }
}
