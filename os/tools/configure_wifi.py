#!/usr/bin/env python3
"""Private Terminal Wi-Fi setup for offline/headless media preparation."""
import argparse
import getpass
import hashlib
import os
from pathlib import Path
import re

def configuration(ssid,password,country='US',hidden=False):
    encoded=ssid.encode('utf-8')
    if not 1<=len(encoded)<=32 or not 8<=len(password)<=63 or any(ord(c)<32 or ord(c)>126 for c in password):raise ValueError('Use a 1–32 byte Wi-Fi name and an 8–63 character printable ASCII passphrase')
    if not re.fullmatch(r'[A-Z]{2}',country):raise ValueError('Use a two-letter country code')
    psk=hashlib.pbkdf2_hmac('sha1',password.encode(),encoded,4096,32).hex()
    quoted=password.replace('\\','\\\\').replace('"','\\"')
    return f'country={country}\nctrl_interface=/run/wpa_supplicant\nupdate_config=0\nnetwork={{\n ssid={encoded.hex()}\n psk={psk}\n sae_password="{quoted}"\n key_mgmt=WPA-PSK SAE\n ieee80211w=1\n scan_ssid={int(hidden)}\n}}\n'

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--output',required=True,type=Path);args=parser.parse_args()
    ssid=input('Home Wi-Fi network name: ')
    password=getpass.getpass('Wi-Fi passphrase (hidden): ')
    if getpass.getpass('Confirm Wi-Fi passphrase: ')!=password:raise ValueError('Passphrases do not match')
    country=input('Two-letter country code [US]: ').strip().upper() or 'US'
    hidden=input('Is this a hidden Wi-Fi network? [y/N]: ').strip().lower()=='y'
    text=configuration(ssid,password,country,hidden)
    args.output.parent.mkdir(parents=True,exist_ok=True);args.output.parent.chmod(0o700)
    descriptor=os.open(args.output,os.O_WRONLY|os.O_CREAT|os.O_TRUNC|os.O_NOFOLLOW,0o600)
    with os.fdopen(descriptor,'w') as output:output.write(text)
    args.output.chmod(0o600)
    print('Wi-Fi setup saved privately. No network credentials were printed.')

if __name__=='__main__':main()
