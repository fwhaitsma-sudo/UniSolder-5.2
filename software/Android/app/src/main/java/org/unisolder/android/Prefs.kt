package org.unisolder.android

import android.content.Context

/** Small settings store of the app (alerts, tip baselines, saved PID sets). */
class Prefs(ctx: Context) {
    private val sp = ctx.getSharedPreferences("unisolder", Context.MODE_PRIVATE)

    fun getBool(key: String, def: Boolean): Boolean = sp.getBoolean(key, def)
    fun putBool(key: String, v: Boolean) = sp.edit().putBoolean(key, v).apply()
    fun getFloat(key: String, def: Float): Float = sp.getFloat(key, def)
    fun putFloat(key: String, v: Float) = sp.edit().putFloat(key, v).apply()
    fun getString(key: String, def: String): String = sp.getString(key, def) ?: def
    fun putString(key: String, v: String) = sp.edit().putString(key, v).apply()
}
