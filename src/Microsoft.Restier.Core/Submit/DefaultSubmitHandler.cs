﻿// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Restier.Core.Submit
{
    /// <summary>
    /// The default handler for submitting changes through the <see cref="ApiBase"/>.
    /// </summary>
    internal class DefaultSubmitHandler
    {

        #region Private Members

        private readonly IChangeSetInitializer initializer;
        private readonly IChangeSetItemAuthorizer authorizer;
        private readonly IChangeSetItemValidator validator;
        private readonly IChangeSetItemFilter filter;
        private readonly ISubmitExecutor executor;

        #endregion

        #region Constructors

        /// <summary>
        /// 
        /// </summary>
        /// <param name="serviceProvider"></param>
        public DefaultSubmitHandler(IServiceProvider serviceProvider)
        {
            //RWM: This stuff SHOULD be getting passed into a constructor. But the DI implementation is less than awesome.
            //     So we'll work around it for now and still save some allocations.
            initializer = serviceProvider.GetService<IChangeSetInitializer>();
            executor = serviceProvider.GetService<ISubmitExecutor>();
            authorizer = serviceProvider.GetService<IChangeSetItemAuthorizer>();
            validator = serviceProvider.GetService<IChangeSetItemValidator>();
            filter = serviceProvider.GetService<IChangeSetItemFilter>();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Asynchronously executes the submit flow.
        /// </summary>
        /// <param name="context">
        /// The submit context.
        /// </param>
        /// <param name="cancellationToken">
        /// A cancellation token.
        /// </param>
        /// <returns>
        /// A task that represents the asynchronous
        /// operation whose result is a submit result.
        /// </returns>
        public async Task<SubmitResult> SubmitAsync(SubmitContext context, CancellationToken cancellationToken)
        {
            Ensure.NotNull(context, nameof(context));

            if (initializer == null)
            {
                throw new NotSupportedException(Resources.ChangeSetPreparerMissing);
            }

            await initializer.InitializeAsync(context, cancellationToken).ConfigureAwait(false);

            if (context.Result != null)
            {
                return context.Result;
            }

            var eventsChangeSet = context.ChangeSet;

            IEnumerable<ChangeSetItem> currentChangeSetItems = eventsChangeSet.Entries.ToArray();

            await PerformValidate(context, currentChangeSetItems, cancellationToken).ConfigureAwait(false);

            await PerformPreEvent(context, currentChangeSetItems, cancellationToken).ConfigureAwait(false);

            await PerformPersist(context, cancellationToken).ConfigureAwait(false);

            context.ChangeSet.Entries.Clear();

            await PerformPostEvent(context, currentChangeSetItems, cancellationToken).ConfigureAwait(false);

            return context.Result;
        }

        #endregion

        #region Private Methods

        private static string GetAuthorizeFailedMessage(ChangeSetItem item)
        {
            switch (item.Type)
            {
                case ChangeSetItemType.DataModification:
                    var dataModification = (DataModificationItem)item;
                    string message = null;
                    if (dataModification.EntitySetOperation == RestierEntitySetOperation.Insert)
                    {
                        message = Resources.NoPermissionToInsertEntity;
                    }
                    else if (dataModification.EntitySetOperation == RestierEntitySetOperation.Update)
                    {
                        message = Resources.NoPermissionToUpdateEntity;
                    }
                    else if (dataModification.EntitySetOperation == RestierEntitySetOperation.Delete)
                    {
                        message = Resources.NoPermissionToDeleteEntity;
                    }
                    else
                    {
                        throw new NotSupportedException(Resources.DataModificationMustBeCUD);
                    }

                    return string.Format(CultureInfo.InvariantCulture, message, dataModification.ResourceSetName);

                default:
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, Resources.InvalidChangeSetEntryType, item.Type));
            }
        }

        private async Task PerformValidate(SubmitContext context, IEnumerable<ChangeSetItem> changeSetItems, CancellationToken cancellationToken)
        {
            await InvokeAuthorizers(context, changeSetItems, cancellationToken).ConfigureAwait(false);

            await InvokeValidators(context, changeSetItems, cancellationToken).ConfigureAwait(false);

            foreach (var item in changeSetItems.Where(i => i.HasChanged()))
            {
                if (item.ChangeSetItemProcessingStage == ChangeSetItemProcessingStage.ChangedWithinOwnPreEventing)
                {
                    item.ChangeSetItemProcessingStage = ChangeSetItemProcessingStage.PreEvented;
                }
                else
                {
                    item.ChangeSetItemProcessingStage = ChangeSetItemProcessingStage.Validated;
                }
            }
        }

        private async Task InvokeAuthorizers(SubmitContext context, IEnumerable<ChangeSetItem> changeSetItems, CancellationToken cancellationToken)
        {
            if (authorizer == null)
            {
                return;
            }

            foreach (var item in changeSetItems.Where(i => i.HasChanged()))
            {
                if (!await authorizer.AuthorizeAsync(context, item, cancellationToken).ConfigureAwait(false))
                {
                    throw new SecurityException(GetAuthorizeFailedMessage(item));
                }
            }
        }

        private async Task InvokeValidators(SubmitContext context, IEnumerable<ChangeSetItem> changeSetItems, CancellationToken cancellationToken)
        {
            if (validator == null)
            {
                return;
            }

            var validationResults = new Collection<ChangeSetItemValidationResult>();

            foreach (var entry in changeSetItems.Where(i => i.HasChanged()))
            {
                await validator.ValidateChangeSetItemAsync(context, entry, validationResults, cancellationToken).ConfigureAwait(false);
            }

            var errors = validationResults.Where(result => result.Severity == EventLevel.Error);

            if (errors.Any())
            {
                var validationErrorMessage = Resources.ValidationFailsTheOperation;
                throw new ChangeSetValidationException(validationErrorMessage)
                {
                    ValidationResults = errors
                };
            }
        }

        private async Task PerformPreEvent(SubmitContext context, IEnumerable<ChangeSetItem> changeSetItems, CancellationToken cancellationToken)
        {
            if (filter == null)
            {
                return;
            }

            foreach (var item in changeSetItems)
            {
                if (item.ChangeSetItemProcessingStage == ChangeSetItemProcessingStage.Validated)
                {
                    item.ChangeSetItemProcessingStage = ChangeSetItemProcessingStage.PreEventing;


                    if (filter != null)
                    {
                        await filter.OnChangeSetItemProcessingAsync(context, item, cancellationToken).ConfigureAwait(false);
                    }

                    if (item.ChangeSetItemProcessingStage == ChangeSetItemProcessingStage.PreEventing)
                    {
                        // if the state is still the intermediate state,
                        // the entity was not changed during processing
                        // and can move to the next step
                        item.ChangeSetItemProcessingStage = ChangeSetItemProcessingStage.PreEvented;
                    }
                    else if (item.ChangeSetItemProcessingStage == ChangeSetItemProcessingStage.Initialized /*&&
                        entity.Details.EntityState == originalEntityState*/)
                    {
                        item.ChangeSetItemProcessingStage = ChangeSetItemProcessingStage.ChangedWithinOwnPreEventing;
                    }
                }
            }
        }

        private async Task PerformPersist(SubmitContext context, CancellationToken cancellationToken)
        {
            if (executor == null)
            {
                throw new NotSupportedException(Resources.SubmitExecutorMissing);
            }

            context.Result = await executor.ExecuteSubmitAsync(context, cancellationToken).ConfigureAwait(false);
        }

        private async Task PerformPostEvent(SubmitContext context, IEnumerable<ChangeSetItem> changeSetItems, CancellationToken cancellationToken)
        {
            if (filter == null)
            {
                return;
            }

            foreach (var item in changeSetItems)
            {
                await filter.OnChangeSetItemProcessedAsync(context, item, cancellationToken).ConfigureAwait(false);
            }
        }

        #endregion

    }

}