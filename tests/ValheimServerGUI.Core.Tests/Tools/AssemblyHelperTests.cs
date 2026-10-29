using ValheimServerGUI.Tools;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Tools
{
    /// <summary>
    /// Locks the version-precedence behavior the update notification depends on. MainWindow shows
    /// "Update available" when <see cref="AssemblyHelper.CompareVersions"/> returns a positive value
    /// (the latest release is newer than the running version), so these tests assert that the two
    /// release-notification requirements hold under semantic-version precedence.
    /// </summary>
    public class AssemblyHelperTests
    {
        // Requirement 1: a user on the current stable (2.3.1) must be notified when the
        // pre-release 2.4.0-rc.1 is published.
        [Fact]
        public void CompareVersions_StableUser_SeesReleaseCandidate_AsUpdate()
        {
            Assert.Equal(1, AssemblyHelper.CompareVersions("2.3.1", "2.4.0-rc.1"));
        }

        // Requirement 2: a user on 2.4.0-rc.1 must be notified when 2.4.0 proper is published.
        // This is the case a naive string comparison gets WRONG -- "2.4.0" sorts BEFORE
        // "2.4.0-rc.1" lexically -- but semver precedence ranks the final release above its
        // own pre-releases.
        [Fact]
        public void CompareVersions_ReleaseCandidateUser_SeesFinalRelease_AsUpdate()
        {
            Assert.Equal(1, AssemblyHelper.CompareVersions("2.4.0-rc.1", "2.4.0"));
        }

        [Theory]
        // A later release candidate is an update over an earlier one.
        [InlineData("2.4.0-rc.1", "2.4.0-rc.2")]
        // Release tags carry a leading "v"; it must still parse and be recognized as newer.
        [InlineData("2.3.1", "v2.4.0-rc.1")]
        // The alpha numbering replaced the fork's 3.x line: it is newer than every 3.x release, including the bridge.
        [InlineData("3.9.0", "v0.0.1-alpha")]
        [InlineData("3.8.1", "0.0.1-alpha")]
        [InlineData("0.0.1-alpha", "0.0.2-alpha")]
        [InlineData("0.0.9-alpha", "0.0.10-alpha")]
        [InlineData("0.0.9-alpha", "0.1.0-alpha")]
        [InlineData("0.0.9-alpha", "1.0.0")]
        // The bridge release itself is still a plain update for the old 3.x copies.
        [InlineData("3.8.1", "v3.9.0")]
        public void CompareVersions_NewerOther_ReturnsOne(string current, string other)
        {
            Assert.Equal(1, AssemblyHelper.CompareVersions(current, other));
        }

        [Theory]
        // Running a pre-release while the latest ordinary release is still the old stable:
        // the running build is "ahead", which MainWindow surfaces as a pre-release build.
        [InlineData("2.4.0-rc.1", "2.3.1")]
        // Running the final while an rc is (incorrectly) reported as latest.
        [InlineData("2.4.0", "2.4.0-rc.1")]
        // An alpha copy never goes back to the old 3.x numbering.
        [InlineData("0.0.1-alpha", "v3.9.0")]
        [InlineData("1.0.0", "3.8.1")]
        public void CompareVersions_OlderOther_ReturnsNegativeOne(string current, string other)
        {
            Assert.Equal(-1, AssemblyHelper.CompareVersions(current, other));
        }

        [Theory]
        [InlineData("2.4.0-rc.1", "2.4.0-rc.1")]
        [InlineData("2.3.1", "v2.3.1")]
        public void CompareVersions_SameVersion_ReturnsZero(string current, string other)
        {
            Assert.Equal(0, AssemblyHelper.CompareVersions(current, other));
        }

        [Theory]
        [InlineData("2.3.1", "not-a-version")]
        [InlineData("garbage", "2.4.0")]
        public void CompareVersions_Unparseable_ReturnsSentinel(string current, string other)
        {
            Assert.Equal(-2, AssemblyHelper.CompareVersions(current, other));
        }

        // An informational version with no "+build" suffix must come back unchanged, not throw (an unguarded
        // IndexOf returns -1 and the range throws). The test host's assembly has no build suffix, so calling it
        // exercises that case.
        [Fact]
        public void GetApplicationVersion_NoBuildSuffix_DoesNotThrow()
        {
            var version = AssemblyHelper.GetApplicationVersion();

            Assert.False(string.IsNullOrWhiteSpace(version));
            Assert.DoesNotContain("+build", version);
        }
    }
}
