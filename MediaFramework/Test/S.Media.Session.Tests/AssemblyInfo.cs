using Xunit;

// The ShowSession tests spin up real background compositor/transport pumps and assert on timing-sensitive
// state (fade ramps, pause/resume settling, first-frame canvas scaling). Running them concurrently on a
// 2-core CI runner starves those pumps and flakes the assertions - serialize this assembly so each test
// gets uncontended cores. See project_flaky_timing_tests memory.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
