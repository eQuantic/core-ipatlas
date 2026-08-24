// Release automation (semantic-release). Analyzes house-style commits, computes the next
// semver, updates CHANGELOG.md + Directory.Build.props, packs, publishes to NuGet and
// creates the tagged GitHub release.

// House commits carry a leading gitmoji (`✨ feat: …`, `🐛 fix: …`); the stock
// conventional-commits patterns reject that prefix, so these make it optional.
const parserOpts = {
  headerPattern: /^(?:[^\w\s]+ )?(\w+)(?:\(([^)]*)\))?!?: (.+)$/,
  breakingHeaderPattern: /^(?:[^\w\s]+ )?(\w+)(?:\(([^)]*)\))?!: (.+)$/,
  headerCorrespondence: ['type', 'scope', 'subject'],
};

export default {
  branches: [
    'main',
    { name: 'preview', prerelease: 'preview' },
  ],
  tagFormat: 'v${version}',
  plugins: [
    ['@semantic-release/commit-analyzer', { preset: 'conventionalcommits', parserOpts }],
    ['@semantic-release/release-notes-generator', { preset: 'conventionalcommits', parserOpts }],
    ['@semantic-release/changelog', {
      changelogFile: 'CHANGELOG.md',
      changelogTitle: '# Changelog',
    }],
    ['@semantic-release/exec', {
      verifyConditionsCmd: 'bash .github/scripts/release-verify.sh',
      prepareCmd: 'bash .github/scripts/release-prepare.sh ${nextRelease.version}',
      publishCmd: 'bash .github/scripts/release-publish.sh',
    }],
    ['@semantic-release/git', {
      assets: ['CHANGELOG.md', 'Directory.Build.props'],
      message: '🔧 chore: release v${nextRelease.version} [skip ci]',
    }],
    ['@semantic-release/github', {
      assets: [
        { path: 'artifacts/packages/*.nupkg' },
        { path: 'artifacts/packages/*.snupkg' },
      ],
      // GitHub rejects creating the default 'semantic-release' label on this repo,
      // which would abort the failure-issue notification — create issues unlabeled.
      labels: false,
    }],
  ],
};
