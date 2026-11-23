"""Removes the wrong RuntimeInformation redirection from the .config files.

It works on the text: rewriting it with an XML parser moves the namespaces and Windows stops accepting the file
("side-by-side configuration is incorrect").
"""
import re
import sys

BLOCK = re.compile(r'\s*<assemblyBinding\b[^>]*>(?:(?!</assemblyBinding>).)*?'
                   r'System\.Runtime\.InteropServices\.RuntimeInformation.*?</assemblyBinding>', re.S)

for path in sys.argv[1:]:
    with open(path, encoding='utf-8-sig', newline='') as f:
        text = f.read()
    new, count = BLOCK.subn('', text)
    if count:
        with open(path, 'w', encoding='utf-8', newline='') as f:
            f.write(new)
        print('fixed', path, count)
