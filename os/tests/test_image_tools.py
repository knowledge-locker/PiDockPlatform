import hashlib
import importlib.util
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest

TOOLS=Path(__file__).resolve().parents[1]/"tools"
sys.path.insert(0,str(TOOLS))
import image_components as image
import sign_boot

class ImageToolsTests(unittest.TestCase):
    def test_sysroot_symlinks_never_resolve_against_the_builder_host(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)
            (root/"usr/lib").mkdir(parents=True)
            (root/"lib").symlink_to("/usr/lib")
            (root/"usr/lib/value").write_text("target library")
            self.assertEqual(root/"usr/lib/value",image.resolve_target(root,"/lib/value"))
            (root/"escape").symlink_to("../../etc")
            with self.assertRaises(ValueError): image.resolve_target(root,"/escape/passwd")
            (root/"loop").symlink_to("loop")
            with self.assertRaises(ValueError): image.resolve_target(root,"/loop")
    def test_cpio_is_deterministic_and_has_root_ownership_and_executable_init(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)
            (root/"init").write_text("#!/bin/sh\n")
            (root/"init").chmod(0o755)
            first=image.newc(root,1234)
            os.utime(root/"init",(9999,9999))
            self.assertEqual(first,image.newc(root,1234))
            self.assertEqual(b"070701",first[:6])
            fields=[int(first[n:n+8],16) for n in range(6,110,8)]
            self.assertEqual(0,fields[2]);self.assertEqual(0,fields[3]);self.assertEqual(1234,fields[5])
            self.assertEqual(0o100755,fields[1])
            self.assertIn(b"TRAILER!!!\0",first)
    @unittest.skipUnless(shutil.which("openssl"),"OpenSSL is required")
    def test_rsa_boot_signature_rejects_wrong_key_and_changed_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary); key=root/"key"; public=root/"public"; boot=root/"boot.img"
            subprocess.run(["openssl","genrsa","-out",str(key),"2048"],check=True,capture_output=True)
            public.write_text(image.run("openssl","pkey","-in",key,"-pubout"))
            boot.write_bytes(os.urandom(1024))
            signature=sign_boot.sign(boot,key,1234)
            sign_boot.verify(boot,signature,public)
            other=root/"other-key"
            subprocess.run(["openssl","genrsa","-out",str(other),"2048"],check=True,capture_output=True)
            other_public=root/"other-public"
            other_public.write_text(image.run("openssl","pkey","-in",other,"-pubout"))
            with self.assertRaises(subprocess.CalledProcessError): sign_boot.verify(boot,signature,other_public)
            with self.assertRaises(ValueError): sign_boot.verify(boot,signature.replace("ts: 1234","ts: invalid"),public)
            boot.write_bytes(b"changed")
            with self.assertRaises(ValueError): sign_boot.verify(boot,signature,public)
            forged=signature.splitlines();forged[0]=image.digest(boot)
            with self.assertRaises(subprocess.CalledProcessError): sign_boot.verify(boot,"\n".join(forged)+"\n",public)
    @unittest.skipUnless(shutil.which("veritysetup") and shutil.which("mkfs.ext4"),"Linux verity utilities are required")
    def test_verity_checks_the_root_bytes_and_detects_corruption_without_mapping_a_device(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary); source=root/"root.ext4"; verified=root/"root.verity"
            with source.open("wb") as output:output.truncate(32<<20)
            image.run("mkfs.ext4","-q","-F",source)
            root_hash,offset=image.verity(source,verified,1234)
            self.assertEqual(32<<20,offset)
            self.assertEqual(64,len(root_hash))
            with verified.open("r+b") as target:target.seek(4096);target.write(b"tampered")
            with self.assertRaises(subprocess.CalledProcessError):
                image.run("veritysetup","verify",verified,verified,root_hash,"--hash-offset="+str(offset))

if __name__=="__main__":unittest.main()

class FlashSafetyTests(unittest.TestCase):
    def test_writer_rejects_nonremovable_media_changed_cards_and_partition_paths(self):
        import flash_macos
        media={'BSD Name':'disk16','Whole':True,'Removable':True,'Writable':True,'Size':64000}
        identity={'bsd_name':'disk16','size_in_bytes':64000,'spcardreader_card_serialnumber':'card-serial'}
        flash_macos.validate('disk16',media,identity,'card-serial',64000,60000)
        for changed in ({**media,'Removable':False},{**media,'Whole':False},{**media,'Size':1000000}):
            with self.assertRaises(ValueError): flash_macos.validate('disk16',changed,identity,'card-serial',64000,60000)
        with self.assertRaises(ValueError): flash_macos.validate('disk16s1',media,identity,'card-serial',64000,60000)
        with self.assertRaises(ValueError): flash_macos.validate('disk0',media,identity,'card-serial',64000,60000)
        with self.assertRaises(ValueError): flash_macos.validate('disk16',media,identity,'different-card',64000,60000)
        with self.assertRaises(ValueError): flash_macos.validate('disk16',media,identity,'card-serial',64000,65000)
