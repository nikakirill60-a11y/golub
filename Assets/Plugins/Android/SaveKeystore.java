package ru.blitzlegacy.save;

import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import android.util.Log;

import java.nio.ByteBuffer;
import java.security.KeyStore;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/**
 * Wraps the save data key with a non-exportable AES-256-GCM key in the
 * Android Keystore. Called from BlitzLegacy.Runtime.Save.AndroidKeystoreProtector.
 * Both methods return "" on any failure; the game then treats the saves as
 * unreadable and starts over.
 */
public final class SaveKeystore {
    private static final String TAG = "BlitzSaves";
    private static final String PROVIDER = "AndroidKeyStore";
    private static final String ALIAS = "blitz_legacy_save_v1";
    private static final String TRANSFORMATION = "AES/GCM/NoPadding";
    private static final int TAG_BITS = 128;

    private SaveKeystore() {
    }

    /** Replaces the keystore key with a fresh one and returns base64(ivLength | iv | ciphertext). */
    public static String wrap(String plainBase64) {
        try {
            byte[] plain = Base64.decode(plainBase64, Base64.NO_WRAP);
            KeyStore store = KeyStore.getInstance(PROVIDER);
            store.load(null);
            if (store.containsAlias(ALIAS)) {
                store.deleteEntry(ALIAS);
            }

            KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, PROVIDER);
            generator.init(new KeyGenParameterSpec.Builder(
                    ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256)
                    .setRandomizedEncryptionRequired(true)
                    .build());
            SecretKey key = generator.generateKey();

            Cipher cipher = Cipher.getInstance(TRANSFORMATION);
            cipher.init(Cipher.ENCRYPT_MODE, key);
            byte[] iv = cipher.getIV();
            byte[] sealed = cipher.doFinal(plain);
            ByteBuffer out = ByteBuffer.allocate(1 + iv.length + sealed.length);
            out.put((byte) iv.length);
            out.put(iv);
            out.put(sealed);
            return Base64.encodeToString(out.array(), Base64.NO_WRAP);
        } catch (Exception e) {
            Log.w(TAG, "wrap failed", e);
            return "";
        }
    }

    /** The base64 data key, or "" when the keystore key is gone or the blob is not ours. */
    public static String unwrap(String wrappedBase64) {
        try {
            byte[] blob = Base64.decode(wrappedBase64, Base64.NO_WRAP);
            if (blob.length < 2) {
                return "";
            }

            int ivLength = blob[0] & 0xff;
            if (ivLength < 12 || blob.length <= 1 + ivLength) {
                return "";
            }

            KeyStore store = KeyStore.getInstance(PROVIDER);
            store.load(null);
            KeyStore.Entry entry = store.getEntry(ALIAS, null);
            if (!(entry instanceof KeyStore.SecretKeyEntry)) {
                return "";
            }

            SecretKey key = ((KeyStore.SecretKeyEntry) entry).getSecretKey();
            Cipher cipher = Cipher.getInstance(TRANSFORMATION);
            cipher.init(Cipher.DECRYPT_MODE, key, new GCMParameterSpec(TAG_BITS, blob, 1, ivLength));
            byte[] plain = cipher.doFinal(blob, 1 + ivLength, blob.length - 1 - ivLength);
            return Base64.encodeToString(plain, Base64.NO_WRAP);
        } catch (Exception e) {
            Log.w(TAG, "unwrap failed", e);
            return "";
        }
    }
}
