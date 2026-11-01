using Wings.Routing.Engine;
using Wings.Routing.Model;
using static Wings.Routing.Core.Tests.TestTopology;

namespace Wings.Routing.Core.Tests;

public class RoutingEngineTests
{
    [Fact]
    public void ScenarioA_user_assignment_has_highest_priority()
    {
        var d = new TestTopology().Route(Abc, AbcUser1);
        Assert.Equal(RoutingOutcome.Routed, d.Outcome);
        Assert.Equal(S1, d.ServerId);
        Assert.Equal(RoutingSource.UserAssignment, d.Source);
    }

    [Fact]
    public void ScenarioB_user_without_assignment_uses_company_server()
    {
        var d = new TestTopology().Route(Abc, AbcUser2);
        Assert.Equal(S2, d.ServerId);
        Assert.Equal(RoutingSource.CompanyAssignment, d.Source);
    }

    [Fact]
    public void ScenarioC_company_without_dedicated_server_uses_authorised_shared_pool()
    {
        var d = new TestTopology().Route(Xyz, XyzManager);
        Assert.Equal(RoutingSource.SharedPool, d.Source);
        Assert.Contains(d.ServerId!.Value, new[] { S3, S4, S5 });
        Assert.Equal(PoolA, d.PoolId);
    }

    [Fact]
    public void ScenarioD_failed_user_server_without_failover_policy_returns_no_healthy_destination()
    {
        var t = new TestTopology().SetHealth(S1, HealthState.Unhealthy);
        var d = t.Route(Abc, AbcUser1);
        Assert.Equal(RoutingOutcome.NoHealthyDestination, d.Outcome);
        Assert.Null(d.ServerId);
    }

    [Fact]
    public void ScenarioD_failed_company_server_fails_over_to_permitted_secondary()
    {
        var t = new TestTopology().SetHealth(S2, HealthState.Unhealthy);
        var d = t.Route(Abc, AbcUser2);
        Assert.Equal(S6, d.ServerId);
        Assert.True(d.IsFailover);
    }

    [Fact]
    public void Failover_disabled_policy_never_moves_traffic()
    {
        var t = new TestTopology().SetHealth(S2, HealthState.Unhealthy);
        t.CompanyAssignments[0] = t.CompanyAssignments[0] with { FailoverPolicyId = FailoverOff };
        var d = t.Route(Abc, AbcUser2);
        Assert.Equal(RoutingOutcome.NoHealthyDestination, d.Outcome);
        Assert.Contains("failover is disabled", d.Reason);
    }

    [Fact]
    public void Unauthorised_failover_destination_dedicated_to_another_tenant_is_never_used()
    {
        // Primary and secondary down. Failover pool lists SERVER-07 (DEF's dedicated server) first, then SERVER-06.
        var t = new TestTopology().SetHealth(S2, HealthState.Unhealthy).SetHealth(S6, HealthState.Unhealthy);
        var d = t.Route(Abc, AbcUser2);
        Assert.Equal(RoutingOutcome.NoHealthyDestination, d.Outcome);
        Assert.Contains(d.Trace, line => line.Contains("SERVER-07") && line.Contains("isolation"));
    }

    [Fact]
    public void Dedicated_tenant_is_never_sent_to_unrelated_shared_pool_when_primary_fails()
    {
        var t = new TestTopology().SetHealth(S2, HealthState.Unhealthy).SetHealth(S6, HealthState.Unhealthy)
            .SetHealth(S7Def, HealthState.Unhealthy);
        var d = t.Route(Abc, AbcUser2);
        Assert.Equal(RoutingOutcome.NoHealthyDestination, d.Outcome);
        Assert.DoesNotContain(new[] { S3, S4, S5 }, s => s == d.ServerId);
    }

    [Fact]
    public void Max_failover_attempts_limits_candidates()
    {
        var t = new TestTopology().SetHealth(S2, HealthState.Unhealthy).SetHealth(S6, HealthState.Unhealthy);
        t.FailoverPolicies[0] = t.FailoverPolicies[0] with { MaxFailoverAttempts = 1 };
        var d = t.Route(Abc, AbcUser2);
        Assert.Equal(RoutingOutcome.NoHealthyDestination, d.Outcome);
        Assert.Contains(d.Trace, l => l.Contains("Maximum failover attempts"));
    }

    [Fact]
    public void ScenarioF_identical_usernames_in_different_companies_resolve_independently()
    {
        // Usernames resolve to different immutable UserIds per company; routing keys on (CompanyId, UserId).
        var t = new TestTopology();
        var abc = t.Route(Abc, AbcManager);
        var xyz = t.Route(Xyz, XyzManager);
        Assert.Equal(S2, abc.ServerId);
        Assert.Equal(RoutingSource.SharedPool, xyz.Source);

        // A user id from another company never matches this company's user assignment.
        var crossed = t.Route(Xyz, AbcUser1);
        Assert.NotEqual(S1, crossed.ServerId);
    }

    [Fact]
    public void Unknown_company_is_denied()
    {
        var d = new TestTopology().Route(Guid.NewGuid(), AbcUser1);
        Assert.Equal(RoutingOutcome.Denied, d.Outcome);
    }

    [Fact]
    public void Company_without_any_assignment_fails_closed()
    {
        var t = new TestTopology();
        var orphan = Guid.NewGuid();
        t.Companies.Add(new CompanyInfo(orphan, "ORPHAN", "Orphan Ltd", CompanyStatus.Active, CompanyRoutingMode.SharedPool, null, null, null));
        var d = t.Route(orphan, Guid.NewGuid());
        Assert.Equal(RoutingOutcome.NoAssignment, d.Outcome);
        Assert.Null(d.ServerId);
    }

    [Fact]
    public void Disabled_user_and_suspended_company_are_denied()
    {
        var t = new TestTopology();
        t.Blocked.Add(new UserKey(Abc, AbcUser1));
        Assert.Equal(RoutingOutcome.Denied, t.Route(Abc, AbcUser1).Outcome);

        t.Companies[1] = t.Companies[1] with { Status = CompanyStatus.Suspended };
        Assert.Equal(RoutingOutcome.Denied, t.Route(Xyz, XyzManager).Outcome);
    }

    [Fact]
    public void Routing_stays_within_requested_environment()
    {
        var t = new TestTopology();
        // XYZ has no staging assignment; staging must not borrow production servers.
        Assert.Equal(RoutingOutcome.NoAssignment, t.Route(Xyz, XyzManager, env: DeploymentEnvironment.Staging).Outcome);

        t.PoolAssignments.Add(new(Guid.NewGuid(), Xyz, PoolA, null, DeploymentEnvironment.Staging, 0, true, Now.AddDays(-1), null));
        // Pool A is a production pool, so a staging assignment to it is rejected.
        Assert.Equal(RoutingOutcome.NoAssignment, t.Route(Xyz, XyzManager, env: DeploymentEnvironment.Staging).Outcome);
    }

    [Fact]
    public void Application_compatibility_is_enforced()
    {
        var t = new TestTopology().UpdateServer(S2, s => s with { ApplicationIds = [Erp] });
        var d = t.Route(Abc, AbcUser2, app: Hr);
        Assert.Equal(S6, d.ServerId); // SERVER-02 does not host HR; secondary does.
        Assert.True(d.IsFailover);

        t.UpdateServer(S6, s => s with { ApplicationVersion = "1.9.0" }); // HR requires 2.0
        Assert.Equal(RoutingOutcome.NoHealthyDestination, t.Route(Abc, AbcUser2, app: Hr).Outcome);
    }

    [Fact]
    public void Application_specific_assignment_beats_application_wide()
    {
        var t = new TestTopology();
        t.UserAssignments.Add(new(Guid.NewGuid(), Abc, AbcUser1, Hr, DeploymentEnvironment.Production, S6, null, null,
            false, true, Now.AddDays(-1), null));
        Assert.Equal(S6, t.Route(Abc, AbcUser1, app: Hr).ServerId);
        Assert.Equal(S1, t.Route(Abc, AbcUser1, app: Erp).ServerId);
    }

    [Fact]
    public void Time_bound_assignments_respect_effective_window()
    {
        var t = new TestTopology();
        t.UserAssignments[0] = t.UserAssignments[0] with { EffectiveFrom = Now.AddHours(1) };
        Assert.Equal(S2, t.Route(Abc, AbcUser1).ServerId); // not yet effective
        Assert.Equal(S1, t.Route(Abc, AbcUser1, engine: CreateEngine(Now.AddHours(2), new RoutingEngineOptions { LastKnownGoodMaxAge = null })).ServerId);

        t.UserAssignments[0] = t.UserAssignments[0] with { EffectiveFrom = Now.AddDays(-2), EffectiveTo = Now.AddDays(-1) };
        Assert.Equal(S2, t.Route(Abc, AbcUser1).ServerId); // expired
    }

    [Fact]
    public void Draining_server_keeps_existing_sessions_but_takes_no_new_ones()
    {
        var t = new TestTopology().UpdateServer(S3, s => s with { AdminState = ServerAdminState.Draining });

        var existing = t.Route(Xyz, XyzManager, affinity: S3);
        Assert.Equal(S3, existing.ServerId);
        Assert.True(existing.AffinityApplied);

        for (var i = 0; i < 10; i++)
            Assert.NotEqual(S3, t.Route(Xyz, Guid.NewGuid()).ServerId);
    }

    [Fact]
    public void Maintenance_server_is_never_selected_even_with_affinity()
    {
        var t = new TestTopology().UpdateServer(S3, s => s with { AdminState = ServerAdminState.Maintenance });
        Assert.NotEqual(S3, t.Route(Xyz, XyzManager, affinity: S3).ServerId);
    }

    [Fact]
    public void Draining_dedicated_server_moves_new_sessions_to_approved_alternative()
    {
        var t = new TestTopology().UpdateServer(S2, s => s with { AdminState = ServerAdminState.Draining });
        Assert.Equal(S2, t.Route(Abc, AbcUser2, affinity: S2).ServerId);
        Assert.Equal(S6, t.Route(Abc, AbcUser2).ServerId);
    }

    [Fact]
    public void Automatic_failback_returns_session_to_recovered_primary()
    {
        var t = new TestTopology();
        var d = t.Route(Abc, AbcUser2, affinity: S6);
        Assert.Equal(S2, d.ServerId);
        Assert.True(d.IsFailback);
    }

    [Fact]
    public void End_of_session_failback_keeps_session_on_failover_server()
    {
        var t = new TestTopology();
        t.CompanyAssignments[0] = t.CompanyAssignments[0] with { FailoverPolicyId = FailoverOnEndOfSession };
        var d = t.Route(Abc, AbcUser2, affinity: S6);
        Assert.Equal(S6, d.ServerId);
        Assert.True(d.IsFailover);
        Assert.Equal(S2, t.Route(Abc, AbcUser2).ServerId); // new sessions go to the primary
    }

    [Fact]
    public void Affinity_to_server_no_longer_permitted_is_ignored()
    {
        var t = new TestTopology();
        // ABC user pinned to a shared server after an assignment change: re-resolved to the company server.
        var d = t.Route(Abc, AbcUser2, affinity: S3);
        Assert.Equal(S2, d.ServerId);
        Assert.False(d.AffinityApplied);
    }

    [Fact]
    public void Excluded_server_after_transport_failure_triggers_permitted_failover_only()
    {
        var t = new TestTopology();
        Assert.Equal(S6, t.Route(Abc, AbcUser2, excluded: [S2]).ServerId);
        Assert.Equal(RoutingOutcome.NoHealthyDestination, t.Route(Abc, AbcUser1, excluded: [S1]).Outcome);
    }

    [Fact]
    public void User_assignment_can_explicitly_allow_company_route_fallback()
    {
        var t = new TestTopology().SetHealth(S1, HealthState.Unhealthy);
        t.UserAssignments[0] = t.UserAssignments[0] with { AllowCompanyRouteFallback = true };
        var d = t.Route(Abc, AbcUser1);
        Assert.Equal(S2, d.ServerId);
        Assert.Equal(RoutingSource.CompanyAssignment, d.Source);
    }

    [Fact]
    public void Degraded_servers_follow_policy()
    {
        var t = new TestTopology().SetHealth(S2, HealthState.Degraded);
        Assert.Equal(S2, t.Route(Abc, AbcUser2).ServerId);
    }

    [Fact]
    public void Unknown_health_is_not_routable_by_default()
    {
        var t = new TestTopology().SetHealth(S1, HealthState.Unknown);
        Assert.Equal(RoutingOutcome.NoHealthyDestination, t.Route(Abc, AbcUser1).Outcome);
        Assert.Equal(S1, t.Route(Abc, AbcUser1, engine: CreateEngine(options: new RoutingEngineOptions { TreatUnknownHealthAsHealthy = true })).ServerId);
    }

    [Fact]
    public void Capacity_and_reservations_limit_new_sessions_only()
    {
        var t = new TestTopology();
        t.UpdateServer(S3, s => s with { MaxCapacity = 10 }).SetHealth(S3, HealthState.Healthy, sessions: 6);
        t.UpdateServer(S4, s => s with { AdminState = ServerAdminState.Disabled });
        t.UpdateServer(S5, s => s with { AdminState = ServerAdminState.Disabled });
        t.Reservations.Add(new CapacityReservationInfo(S3, Def, 5)); // 5 of 10 reserved for DEF

        Assert.Equal(RoutingOutcome.NoHealthyDestination, t.Route(Xyz, XyzManager).Outcome);
        Assert.Equal(S3, t.Route(Xyz, XyzManager, affinity: S3).ServerId); // existing session continues
    }

    [Fact]
    public void Stale_configuration_beyond_last_known_good_limit_fails_closed()
    {
        var t = new TestTopology();
        var snapshot = t.Snapshot(loadedAt: Now.AddHours(-1));
        var d = CreateEngine(options: new RoutingEngineOptions { LastKnownGoodMaxAge = TimeSpan.FromMinutes(30) })
            .Route(snapshot, t.StateView, new RoutingRequest
            {
                CompanyId = Abc, UserId = AbcUser1, ApplicationId = Erp, Environment = DeploymentEnvironment.Production
            });
        Assert.Equal(RoutingOutcome.ConfigurationStale, d.Outcome);

        snapshot.MarkValidated(Now);
        Assert.Equal(S1, CreateEngine().Route(snapshot, t.StateView, new RoutingRequest
        {
            CompanyId = Abc, UserId = AbcUser1, ApplicationId = Erp, Environment = DeploymentEnvironment.Production
        }).ServerId);
    }

    [Fact]
    public void Application_not_published_in_environment_is_denied()
    {
        var d = new TestTopology().Route(Abc, AbcUser1, app: Hr, env: DeploymentEnvironment.Staging);
        Assert.Equal(RoutingOutcome.Denied, d.Outcome);
    }

    [Fact]
    public void Plan_eligibility_restricts_shared_pools()
    {
        var t = new TestTopology();
        t.Pools[0] = t.Pools[0] with { EligiblePlans = ["Enterprise"] };
        Assert.Equal(RoutingOutcome.NoAssignment, t.Route(Xyz, XyzManager).Outcome);
    }

    [Fact]
    public void Shared_pool_failover_uses_failover_pool_only_when_enabled()
    {
        var t = new TestTopology().SetHealth(S3, HealthState.Unhealthy).SetHealth(S4, HealthState.Unhealthy)
            .SetHealth(S5, HealthState.Unhealthy);
        Assert.Equal(RoutingOutcome.NoHealthyDestination, t.Route(Xyz, XyzManager).Outcome);

        t.Pools[0] = t.Pools[0] with { FailoverPolicyId = FailoverOn };
        var d = t.Route(Xyz, XyzManager);
        Assert.Equal(S6, d.ServerId); // SERVER-07 is dedicated to DEF and skipped
        Assert.True(d.IsFailover);
    }

    [Fact]
    public void Balanced_cluster_spreads_users_over_cluster_members()
    {
        var t = new TestTopology();
        var cluster = Guid.NewGuid();
        t.Pools.Add(new PoolInfo
        {
            Id = cluster, Name = "ABC cluster", Algorithm = LoadBalancingAlgorithm.ConsistentHashCompanyUser,
            Members = [new(S2, null, 0, true), new(S6, null, 1, true)]
        });
        t.CompanyAssignments[0] = t.CompanyAssignments[0] with { ClusterPoolId = cluster, ClusterMode = ClusterMode.Balanced, SecondaryServerId = null };
        var chosen = Enumerable.Range(0, 50).Select(_ => t.Route(Abc, Guid.NewGuid()).ServerId).ToHashSet();
        Assert.Equal(new HashSet<Guid?> { S2, S6 }, chosen);
    }

    [Fact]
    public void ScenarioE_new_snapshot_version_applies_new_assignment()
    {
        var t = new TestTopology();
        Assert.Equal(S2, t.Route(Abc, AbcUser2).ServerId);
        t.CompanyAssignments[0] = t.CompanyAssignments[0] with { PrimaryServerId = S6, SecondaryServerId = null };
        t.Version = 2;
        var d = t.Route(Abc, AbcUser2);
        Assert.Equal(S6, d.ServerId);
        Assert.Equal(2, d.ConfigVersion);
    }

    [Fact]
    public void ScenarioG_independent_engine_instances_make_identical_hash_decisions()
    {
        var t = new TestTopology();
        t.Pools[0] = t.Pools[0] with { Affinity = AffinityMode.PerCompany };
        var gateway1 = CreateEngine();
        var gateway2 = CreateEngine();
        var users = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        var first = users.Select(u => t.Route(Xyz, u, engine: gateway1).ServerId).ToArray();
        var second = users.Select(u => t.Route(Xyz, u, engine: gateway2).ServerId).Reverse().Reverse().ToArray();
        Assert.Equal(first, second);
        Assert.Single(first.Distinct()); // company affinity: all users of the company on one server
    }

    [Fact]
    public void Per_user_affinity_is_stable_and_spreads_users()
    {
        var t = new TestTopology();
        t.Pools[0] = t.Pools[0] with { Affinity = AffinityMode.PerUser };
        var users = Enumerable.Range(0, 60).Select(_ => Guid.NewGuid()).ToArray();
        var a = users.Select(u => t.Route(Xyz, u).ServerId).ToArray();
        var b = users.Select(u => t.Route(Xyz, u).ServerId).ToArray();
        Assert.Equal(a, b);
        Assert.Equal(3, a.Distinct().Count());
    }

    [Fact]
    public void Simulator_trace_contains_reasoning_and_failover_candidate()
    {
        var d = new TestTopology().Route(Abc, AbcUser2);
        Assert.NotEmpty(d.Trace);
        Assert.Equal(S6, d.FailoverCandidateId);
    }

    [Fact]
    public void Company_aliases_resolve_with_normalisation()
    {
        var snapshot = new TestTopology().Snapshot();
        Assert.True(snapshot.TryResolveCompany("  abc   motors ", out var c1));
        Assert.True(snapshot.TryResolveCompany("ABC-MOTORS", out var c2));
        Assert.True(snapshot.TryResolveCompany("abc", out var c3));
        Assert.Equal(Abc, c1.Id);
        Assert.Equal(Abc, c2.Id);
        Assert.Equal(Abc, c3.Id);
        Assert.False(snapshot.TryResolveCompany("abc motor", out _));
    }

    [Fact]
    public void Host_and_path_bindings_resolve_longest_prefix()
    {
        var snapshot = new TestTopology().Snapshot();
        Assert.Equal(Hr, snapshot.ResolveBinding("ERP.example.com:443", "/hr/payroll")!.ApplicationId);
        Assert.Equal(Erp, snapshot.ResolveBinding("erp.example.com", "/hrx")!.ApplicationId);
        Assert.Null(snapshot.ResolveBinding("evil.example.com", "/"));
    }
}

public class AuthorizationTests
{
    [Fact]
    public void User_assignment_to_server_not_authorised_for_company_is_denied()
    {
        var t = new TestTopology();
        t.Authorizations.Clear();
        var d = t.Route(TestTopology.Abc, TestTopology.AbcUser1);
        Assert.Equal(RoutingOutcome.Denied, d.Outcome);
        Assert.Null(d.ServerId);
    }

    [Fact]
    public void Company_assignment_servers_are_implicitly_authorised()
    {
        var snapshot = new TestTopology().Snapshot();
        Assert.True(snapshot.IsAuthorizedFor(TestTopology.Abc, TestTopology.S2));
        Assert.True(snapshot.IsAuthorizedFor(TestTopology.Abc, TestTopology.S6));
        Assert.False(snapshot.IsAuthorizedFor(TestTopology.Xyz, TestTopology.S2));
        Assert.True(snapshot.IsAuthorizedFor(TestTopology.Xyz, TestTopology.S3));
    }
}
