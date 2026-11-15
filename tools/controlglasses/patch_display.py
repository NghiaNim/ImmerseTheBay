#!/usr/bin/env python3
"""Apply a narrow local compatibility patch to an authorized APK's decoded source.

No vendor source, APK, key, or private disassembly is distributed with this tool.
The original HDMI and Beam paths and subsequent vendor/product checks remain.
"""
import argparse
from pathlib import Path


def patch(source: str) -> str:
    start = '.method private _isHdmi(Landroid/view/Display;)Z\n'
    if source.count(start) != 1:
        raise ValueError('Expected exactly one supported display detection method')
    begin = source.index(start)
    end = source.index('.end method', begin)
    method = source[begin:end]
    if ':itb_original_hdmi_check' in method:
        raise ValueError('Patch already applied')
    if '.local ' not in method:
        updated = _patch_register_reuse(method)
        return source[:begin] + updated + source[end:]
    anchor = '    const-string v3, "HDMI"\n'
    # Both inspected builds use v3 as scratch, but Unity's D8 assigns the name
    # and true value to different registers than the vendor's standalone APK.
    layouts = [(8, 'v2', 'v0'), (5, 'v1', 'v2')]
    matches = [(name, yes) for count, name, yes in layouts
               if f'.locals {count}\n' in method
               and f'.local {name}, "name":Ljava/lang/String;' in method
               and f':cond_3\n    return {yes}' in method
               and f'const/4 {yes}, 0x1' in method]
    if len(matches) != 1:
        raise ValueError('Unsupported register layout; inspect before changing it')
    name, yes = matches[0]
    expected = (
        'invoke-virtual {p1}, Landroid/view/Display;->getName()Ljava/lang/String;',
        'invoke-virtual {' + name + ', v3}, Ljava/lang/String;->contains(Ljava/lang/CharSequence;)Z',
        'if-nez v3, :cond_3',
    )
    if method.count(anchor) != 1 or not all(part in method for part in expected):
        raise ValueError('Unsupported method layout; inspect this APK before changing it')
    # v3 is scratch here. Do not accept the phone's primary display. Exact name
    # matching avoids treating unrelated external monitors as XREAL hardware.
    addition = '''    # Local Air 2 Ultra display-name compatibility check.
    invoke-virtual {p1}, Landroid/view/Display;->getDisplayId()I
    move-result v3
    if-eqz v3, :itb_original_hdmi_check
    const-string v3, "Air 2 Ultra"
    invoke-virtual {v3, NAME_REGISTER}, Ljava/lang/String;->equals(Ljava/lang/Object;)Z
    move-result v3
    if-nez v3, :cond_3
    :itb_original_hdmi_check
'''.replace('NAME_REGISTER', name)
    updated = method.replace(anchor, addition + anchor, 1)
    return source[:begin] + updated + source[end:]


def _patch_register_reuse(method: str) -> str:
    # Unity 6000.0.85f2 with AGP 9 strips local names and reuses registers: the
    # Display in p1 is overwritten by its name, v1 holds true and p0 false. Read
    # the id and name before p1 is reused, using only v0/v2 scratch registers.
    get_name = '    invoke-virtual {p1}, Landroid/view/Display;->getName()Ljava/lang/String;\n'
    expected = (
        '.locals 4\n',
        '    const/4 p0, 0x0\n',
        '    const/4 v1, 0x1\n',
        get_name + '\n    move-result-object p1\n',
        '    const-string v0, "HDMI"\n\n    invoke-virtual {p1, v0}, Ljava/lang/String;->contains(Ljava/lang/CharSequence;)Z\n\n    move-result p1\n\n    if-nez p1, :cond_3\n',
        ':cond_3\n    return v1\n',
    )
    if method.count(get_name) != 1 or not all(method.count(part) == 1 for part in expected):
        raise ValueError('Unsupported register layout; inspect before changing it')
    addition = '''    # Local Air 2 Ultra display-name compatibility check.
    invoke-virtual {p1}, Landroid/view/Display;->getDisplayId()I
    move-result v0
    if-eqz v0, :itb_original_hdmi_check
    invoke-virtual {p1}, Landroid/view/Display;->getName()Ljava/lang/String;
    move-result-object v0
    const-string v2, "Air 2 Ultra"
    invoke-virtual {v2, v0}, Ljava/lang/String;->equals(Ljava/lang/Object;)Z
    move-result v0
    if-nez v0, :cond_3
    :itb_original_hdmi_check
'''
    return method.replace(get_name, addition + get_name, 1)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('decoded_directory', type=Path)
    args = parser.parse_args()
    targets = list(args.decoded_directory.glob('smali*/com/xreal/glassesdisplayplugevent/display/DisplayModel.smali'))
    if len(targets) != 1:
        raise ValueError('Expected exactly one decoded DisplayModel class')
    target = targets[0]
    target.write_text(patch(target.read_text()))
    print('Patched only _isHdmi: accept exact Air 2 Ultra name on a non-primary display.')
