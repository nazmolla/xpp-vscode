# Builds the F&O Studio documentation site from the GitHub wiki.
# Usage: python site/build.py <wiki checkout> <output folder for MkDocs sources>
# The wiki is the single source; this converts its pages (wiki links, Home page, sidebar) into an MkDocs project
# with a page description for search engines, then `mkdocs build -f <output>/mkdocs.yml` renders it.
import os
import re
import shutil
import sys

SITE_URL = 'https://nazmolla.github.io/xpp-vscode/docs/'
WIKI_URL = 'https://github.com/nazmolla/xpp-vscode/wiki/'

NAV = [
    ('Start', ['Getting-Started', 'Command-Reference', 'Settings-Reference', 'Troubleshooting', 'FAQ', 'Glossary']),
    ('Scenarios', [
        'Scenario-Set-Up-a-New-Developer-Machine', 'Scenario-Onboard-to-an-Existing-Codebase',
        'Scenario-From-Error-Message-to-Fix', 'Scenario-Performance-Tuning-a-Slow-Process',
        'Scenario-Extend-a-Standard-Form-and-Table', 'Scenario-Build-a-Feature-End-to-End', 'Scenario-Batch-Job-End-to-End',
        'Scenario-Expose-Data-to-an-Integration', 'Scenario-Debug-Standard-Code-on-UDE', 'Scenario-Prepare-and-Ship-a-Release',
        'Scenario-Upgrade-to-a-New-Version', 'Scenario-Report-from-a-Scanned-Document',
        'Scenario-Design-a-Security-Role-and-Reduce-License-Cost', 'Scenario-Investigate-Why-a-Record-Looks-Wrong',
        'Scenario-Work-Safely-with-Copilot', 'Scenario-Review-a-Pull-Request', 'Scenario-Set-Up-ALM-for-a-Team',
        'Scenario-From-Work-Item-to-Deployed-Change']),
    ('Environments', ['Connect-to-Environment', 'Select-Environment', 'Cloud-Hosted-and-VHD-Environments', 'Version-Drift-Guard',
                      'Open-Team-Repository', 'Visual-Studio-Side-by-Side', 'Power-Platform-CLI', 'Sign-In-and-Authentication']),
    ('Application Explorer and projects', [
        'Application-Explorer', 'Open-Element', 'D365FO-Projects', 'Solution-View-and-CSharp-References',
        'Models-and-Model-Dependencies', 'Export-and-Import-Projects', 'Drag-and-Drop', 'Element-History',
        'Compare-Code-and-Metadata', 'Copy-Name-and-Open-in-Browser']),
    ('Designers', ['Element-Designer', 'Properties-Grid-and-Pickers', 'Form-Designer-and-Preview', 'Form-Layout-and-Form-Extensions',
                   'Event-Handlers', 'Label-Editor', 'Label-Tools', 'Entity-Relationship-Diagram', 'Hierarchy-and-Reference-Diagrams']),
    ('Code', ['XPP-Code-View', 'Go-to-Definition', 'Find-All-References', 'Implementations-and-Chain-of-Command', 'Code-Lens',
              'Completion-and-Signature-Help', 'Hover', 'Syntax-Check-and-Obsolete-Warnings', 'Formatting-and-Folding',
              'Call-and-Type-Hierarchy', 'Outline-and-Workspace-Symbols', 'Code-Intelligence-Settings', 'Rename-and-Refactoring',
              'Snippets']),
    ('Creating elements', ['New-Element', 'Create-Extension', 'Create-Code-Extension', 'Wizard-Batch-Job', 'Wizard-Report',
                           'Wizard-Approval-Workflow', 'Wizard-Custom-Service', 'Wizard-Business-Event', 'Wizard-Number-Sequence',
                           'Wizard-Feature-Management', 'Wizard-Data-Entity', 'Wizard-Table-with-Form']),
    ('Productivity', ['Search-Code', 'Find-Similar-Implementations', 'Find-Message-Source', 'Quick-Fixes', 'Run-Snippet',
                      'Generate-SysTest-Class', 'Add-to-Privilege', 'Find-Tag-References']),
    ('Build, test and deploy', ['Build-Project-and-Model', 'Compiler-Diagnostics', 'Build-Tasks', 'Best-Practice-Checks', 'Run-Tests',
                                'Test-Explorer', 'Deploy-to-Environment', 'Synchronize-Database', 'Create-Deployment-Package',
                                'Status-Bar']),
    ('Debugging', ['Debugger', 'Breakpoints-and-Source-Mapping', 'Debugging-Standard-Code', 'Debugging-CSharp-Code',
                   'Debugging-on-Cloud-Hosted', 'Infolog', 'Debugger-Options']),
    ('Data', ['Database-Access', 'Browse-Table-Data', 'SQL-Query-Panel', 'Explain-Plan', 'Database-Performance-Insights',
              'Open-in-SSMS', 'Try-Data-Entity-OData', 'Try-Service-Operation']),
    ('Performance', ['Analyze-Performance', 'Performance-Rules-Reference', 'Analyze-Trace', 'Telemetry-Queries']),
    ('Review and release', ['Review-My-Changes', 'Show-Changed-Elements', 'Pre-Deploy-Checklist', 'Upgrade-Readiness',
                            'Standard-Code-Drift', 'Results-View']),
    ('Security and licensing', ['Security-View', 'Analyze-License', 'Who-Can-Access', 'Find-Security-Gaps', 'Segregation-of-Duties',
                                'Security-Report']),
    ('Reports', ['Report-Element-Designer', 'Report-Datasets', 'Report-Designer', 'Report-Preview', 'Scan-Overlay-and-Measure',
                 'Report-Build']),
    ('Copilot and AI', ['Copilot-Agents', 'Agents-for-Developers', 'Agents-for-Functional-Consultants', 'MCP-Agent-Tools',
                        'Agent-Tools-Metadata', 'Agent-Tools-Analysis', 'Agent-Tools-Reports', 'Agent-Tools-Build-and-Data',
                        'Copilot-Activity', 'Copilot-Checkpoints-and-Scope', 'Team-Rules', 'Verify-Changes-in-Running-App',
                        'MCP-Server-Outside-VS-Code']),
    ('ALM', ['ALM-Overview', 'Source-Control-for-XPP', 'Git-Changes-in-the-Project-View', 'Azure-DevOps-and-GitHub-Integration',
             'Work-Items-and-Pipelines', 'Branching-and-Team-Workflow', 'Build-Automation-and-Pipelines',
             'Environments-and-Promotion', 'Models-Packages-and-Versioning']),
    ('Support', ['Report-a-Problem', 'Usage-Statistics', 'Accessibility']),
    ('Technical', ['Architecture', 'Extension-Internals', 'Core-Library', 'Metadata-Server', 'Bridge-Protocol', 'Security-Model',
                   'Building-from-Source', 'Testing-the-Extension', 'Release-Process', 'Compared-with-Visual-Studio']),
]

MKDOCS = """site_name: F&O Studio for VS Code
site_url: {site_url}
site_description: Documentation of F&O Studio, the VS Code extension for Dynamics 365 Finance and Operations X++ development without Visual Studio, with Copilot agents.
repo_url: https://github.com/nazmolla/xpp-vscode
repo_name: nazmolla/xpp-vscode
edit_uri: ''
copyright: F&O Studio is an independent project, not affiliated with or endorsed by Microsoft. Dynamics 365 is a trademark of Microsoft Corporation.
docs_dir: docs
theme:
  name: material
  language: en
  features: [navigation.top, search.suggest, search.highlight, content.code.copy, toc.follow]
  palette:
    - media: "(prefers-color-scheme: light)"
      scheme: default
      primary: indigo
      toggle: {{icon: material/brightness-7, name: Dark mode}}
    - media: "(prefers-color-scheme: dark)"
      scheme: slate
      primary: indigo
      toggle: {{icon: material/brightness-4, name: Light mode}}
plugins: [search]
markdown_extensions:
  - tables
  - admonition
  - attr_list
  - toc: {{permalink: true}}
  - pymdownx.superfences:
      custom_fences:
        - name: mermaid
          class: mermaid
          format: !!python/name:pymdownx.superfences.fence_code_format
extra:
  social:
    - {{icon: fontawesome/brands/github, link: https://github.com/nazmolla/xpp-vscode}}
nav:
{nav}
"""

LINK = re.compile(r'(?<!!)\[([^\]]*)\]\(([^)\s]+)\)')


def title_of(text, page):
    m = re.search(r'^#\s+(.+)$', text, re.M)
    return m.group(1).strip() if m else page.replace('-', ' ')


def description_of(text):
    """First prose paragraph after the title, as plain text, for the meta description."""
    for block in re.split(r'\n\s*\n', text):
        block = block.strip()
        if not block or block[0] in '#!|>-*`[0123456789' or block.startswith('```'):
            continue
        plain = re.sub(r'!\[[^\]]*\]\([^)]*\)', '', block)
        plain = LINK.sub(r'\1', plain)
        plain = re.sub(r'[*_`]', '', plain)
        plain = ' '.join(plain.split())
        if len(plain) > 160:
            plain = plain[:157].rsplit(' ', 1)[0] + '...'
        return plain
    return ''


def convert_links(text, pages):
    def repl(m):
        label, target = m.group(1), m.group(2)
        if target.startswith('#'):
            return f'[{label}](#{re.sub(r"-{2,}", "-", target[1:])})'
        if re.match(r'^(https?:|mailto:)', target):
            return m.group(0)
        page, _, anchor = target.partition('#')
        if page in pages:
            dest = 'index.md' if page == 'Home' else page + '.md'
            # GitHub keeps "--" where a heading had " & "; MkDocs collapses it.
            anchor = re.sub(r'-{2,}', '-', anchor)
            return f'[{label}]({dest}{"#" + anchor if anchor else ""})'
        return m.group(0)
    return LINK.sub(repl, text)


def main(wiki, out):
    docs = os.path.join(out, 'docs')
    shutil.rmtree(out, ignore_errors=True)
    os.makedirs(docs)
    shutil.copytree(os.path.join(wiki, 'images'), os.path.join(docs, 'images'))
    pages = {f[:-3] for f in os.listdir(wiki) if f.endswith('.md') and not f.startswith('_')}
    listed = {p for _, group in NAV for p in group}
    missing = sorted(pages - listed - {'Home'})
    if missing:
        sys.exit('Pages missing from the navigation in site/build.py: ' + ', '.join(missing))
    unknown = sorted(listed - pages)
    if unknown:
        sys.exit('Navigation names pages that are not in the wiki: ' + ', '.join(unknown))
    titles = {}
    for page in pages:
        with open(os.path.join(wiki, page + '.md'), encoding='utf-8') as f:
            text = f.read()
        titles[page] = title_of(text, page)
        body = convert_links(text, pages)
        if page != 'Home':
            body += f'\n\n---\n\n*Source: [{page} on the wiki]({WIKI_URL}{page}).*\n'
        desc = description_of(text).replace('\\', '').replace('"', "'")
        front = f'---\ndescription: "{desc}"\n---\n\n' if desc else ''
        name = 'index.md' if page == 'Home' else page + '.md'
        with open(os.path.join(docs, name), 'w', encoding='utf-8', newline='\n') as f:
            f.write(front + body)
    nav = ['  - Home: index.md']
    for section, group in NAV:
        nav.append(f'  - "{section}":')
        for page in group:
            nav.append(f'      - "{titles[page].replace(chr(34), chr(39))}": {page}.md')
    with open(os.path.join(out, 'mkdocs.yml'), 'w', encoding='utf-8', newline='\n') as f:
        f.write(MKDOCS.format(site_url=SITE_URL, nav='\n'.join(nav)))
    print(f'{len(pages)} pages converted')


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
