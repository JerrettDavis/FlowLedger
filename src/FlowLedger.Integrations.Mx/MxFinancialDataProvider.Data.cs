using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx.Mapping;

namespace FlowLedger.Integrations.Mx;

// Accounts and transactions (#103: split out of MxFinancialDataProvider.cs).
public sealed partial class MxFinancialDataProvider
{
    public async Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(
        string memberProviderId,
        CancellationToken cancellationToken = default)
    {
        var (userGuid, pendingOrMemberGuid) = MxCompositeId.Unpack(memberProviderId);
        var memberGuid = await ResolveMemberGuidAsync(userGuid, pendingOrMemberGuid, cancellationToken).ConfigureAwait(false)
            ?? throw new NeedsUserActionProviderException(
                "No MX member exists yet: complete the Connect widget (POST /api/integrations/mx/connect-token) first.",
                userHint: "Finish connecting your institution in the Connect widget.");

        // Guard: a member needing user action cannot serve account data. This is an intentional
        // defence-in-depth check: a fresh GetMemberStatus call costs one MX API round-trip but
        // avoids returning stale accounts for a member that has been disconnected or challenged
        // since the last sync. The trade-off is accepted — the safety benefit outweighs the cost.
        var member = await _client
            .GetMemberStatusAsync(userGuid, memberGuid, cancellationToken)
            .ConfigureAwait(false);

        // First sync after a Connect-widget flow (the persisted id was still the PENDING placeholder):
        // the widget-created member may still be aggregating. Wait for it (bounded by
        // MaxPollAttempts) instead of reading a half-built account list; CHALLENGED / terminal /
        // poll-cap surface as NeedsUserAction. Steady-state syncs (real member id) are unaffected.
        if (string.Equals(pendingOrMemberGuid, PendingMember, StringComparison.Ordinal))
        {
            member = await WaitForMemberReadyAsync(
                userGuid, member with { Guid = member.Guid ?? memberGuid }, cancellationToken).ConfigureAwait(false);
        }

        if (MxMapper.RequiresUserAction(member.ConnectionStatus))
        {
            throw new NeedsUserActionProviderException(
                $"MX member {memberGuid} requires user action (status {member.ConnectionStatus}).",
                userHint: member.ConnectionStatusMessage,
                providerCode: member.ConnectionStatus);
        }

        var results = new List<ProviderAccount>();
        var page = 1;
        var recordsPerPage = _options.RecordsPerPage;
        var maxPages = _options.MaxPages;

        while (true)
        {
            var response = await _client
                .GetAccountsAsync(userGuid, memberGuid, page, recordsPerPage, cancellationToken)
                .ConfigureAwait(false);

            foreach (var a in response.Accounts ?? [])
            {
                ProviderAccount dto;
                try
                {
                    dto = MxMapper.ToProviderAccount(a);
                }
                catch (UnknownCurrencyException ex)
                {
                    // Never guess USD for an account whose currency the provider did not report (#124).
                    LogSkippingAccount(ex.Message);
                    continue;
                }

                // Re-pack the account id with the owning user so GetTransactions can address it.
                var packedId = MxCompositeId.Pack(userGuid, dto.ProviderId);
                _currencyByAccount[packedId] = dto.CurrencyCode;
                results.Add(dto with { ProviderId = packedId });
            }

            if (!HasMorePages(response.Pagination, page))
            {
                break;
            }

            if (page >= maxPages)
            {
                LogAccountsMaxPages(maxPages, memberGuid, results.Count);
                break;
            }

            page++;
        }

        return results;
    }

    // ── Transactions ──────────────────────────────────────────────────────────

    public async Task<TransactionPage> GetTransactionsAsync(
        string providerAccountId,
        SyncCursor cursor,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        var (userGuid, accountGuid) = MxCompositeId.Unpack(providerAccountId);

        // Cursor encodes (page, records_per_page). pageSize from the caller seeds an initial cursor.
        var effectivePageSize = pageSize > 0 ? pageSize : _options.RecordsPerPage;
        var position = MxCursor.Decode(cursor, effectivePageSize, _logger);

        var response = await _client
            .GetTransactionsAsync(userGuid, accountGuid, position.Page, position.RecordsPerPage, cancellationToken, position.FromDate)
            .ConfigureAwait(false);

        var accountCurrency = _currencyByAccount.GetValueOrDefault(providerAccountId);
        var mapped = new List<ProviderTransaction>();
        var unknownCurrency = 0;
        foreach (var raw in response.Transactions ?? [])
        {
            try
            {
                mapped.Add(MxMapper.ToProviderTransaction(raw, _logger, accountCurrency) with { ProviderAccountId = providerAccountId });
            }
            catch (UnknownCurrencyException ex)
            {
                unknownCurrency++;
                LogSkippingTransaction(ex.Message);
            }
        }

        // A row with no parseable date maps to DateOnly.MinValue (year 0001); never persist that sentinel.
        var items = mapped.Where(t => t.PostedDate != DateOnly.MinValue).ToList();
        var skipped = mapped.Count - items.Count + unknownCurrency;
        foreach (var bad in mapped.Where(t => t.PostedDate == DateOnly.MinValue))
        {
            LogSkippingTransactionNoDate(bad.ProviderId);
        }

        // Without pagination metadata a full page may be truncated: keep paging (a short page ends the pass) so the
        // date watermark is only written once the pass really completed.
        var hasMore = response.Pagination is null
            ? (response.Transactions?.Count ?? 0) >= position.RecordsPerPage
            : HasMorePages(response.Pagination, position.Page);

        // Bound a runaway pass: stop here WITHOUT a watermark and resume from the next page on the next sync, so a
        // truncated pass never moves the date watermark past unread history.
        if (hasMore && position.Page >= Math.Max(1, _options.MaxPages))
        {
            LogTransactionsMaxPages(_options.MaxPages);
            return new TransactionPage(items, position.Next().Encode(), HasMore: false) { SkippedCount = skipped };
        }

        // Mid-pass: advance to the next page (same date window) so a crash resumes here.
        // End of pass: persist a date WATERMARK (page 1, from = today - overlap), never "last page + 1".
        // MX is newest-first, so a parked page cursor would silently skip everything posted later.
        var nextCursor = hasMore
            ? position.Next().Encode()
            : MxCursor.Watermark(
                position.RecordsPerPage,
                DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-Math.Max(0, _options.IncrementalOverlapDays))).Encode();

        return new TransactionPage(items, nextCursor, hasMore) { SkippedCount = skipped };
    }
}
