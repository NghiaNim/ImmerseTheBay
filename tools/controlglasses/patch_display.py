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
