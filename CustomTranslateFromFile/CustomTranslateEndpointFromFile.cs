using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Timers;
using XUnity.AutoTranslator.Plugin.Core.Endpoints;
using XUnity.AutoTranslator.Plugin.Core.Endpoints.Http;
using XUnity.AutoTranslator.Plugin.Core.Web;
using XUnity.Common.Logging;

namespace CustomTranslateFromFile
{
    internal class CustomTranslateEndpointFromFile : ITranslateEndpoint
    {

        private static string filePath;
        private static string outfilePath;
        private static bool observe;

        private static Timer observeTimer;
        private static DateTime lastDictionaryWriteTime;

        private static Dictionary<string, string> wordMap = new Dictionary<string, string>();
        private static Dictionary<Regex, string> regMap = new Dictionary<Regex, string>();
        private static List<string> ignores = new List<string>();
        private static List<Regex> ignoreRegs = new List<Regex>();
        private static List<string> ignoreChecks = new List<string>();

        private static ConcurrentDictionary<string, string> notTransMap = new ConcurrentDictionary<string, string>();

        public string Id => "CustomTranslateFromFile";
        public string FriendlyName => "CustomFromFile";

        public int MaxConcurrency => 100;
        public int MaxTranslationsPerRequest => 1;

        public void Initialize(IInitializationContext context)
        {
            filePath = context.GetOrCreateSetting("CustomFromFile", "FilePath", "chs/dictionary.txt");
            outfilePath = context.GetOrCreateSetting("CustomFromFile", "OutFilePath", "chs/notMatch.txt");
            observe = context.GetOrCreateSetting("CustomFromFile", "Observe", false);

            loadFiles();
            rewriteNotTrans();

            if (observe)
            {
                startObserveFileChange();
            }
        }

        private static void startObserveFileChange()
        {
            if (observeTimer == null)
            {
                observeTimer = new Timer();
                observeTimer.Interval = 1000;
                observeTimer.Elapsed += (sender, e) =>
                {
                    observeFileChange();
                };
                observeTimer.Start();
            }
        }

        private static void stopObserveFileChange()
        {
            if (observeTimer != null)
            {
                observeTimer.Stop();
                observeTimer.Dispose();
                observeTimer = null;
            }
        }

        private static void observeFileChange() {
            if (File.Exists(filePath)) {
                DateTime time = File.GetLastWriteTime(filePath);

                if (lastDictionaryWriteTime != null && lastDictionaryWriteTime.Year > 2000 && !lastDictionaryWriteTime.Equals(time))
                {
                    clearData();
                    loadFiles();
                    rewriteNotTrans();
                }

                lastDictionaryWriteTime = time;
            }
        }

        private static void clearData() {
            wordMap.Clear();
            regMap.Clear();
            ignores.Clear();
            ignoreRegs.Clear();
            ignoreChecks.Clear();
            notTransMap.Clear();
        }

        private static void loadFiles()
        {
            if (File.Exists(filePath))
            {
                string[] lines = File.ReadAllLines(filePath);
                BlockType type = BlockType.word;
                foreach (string lineStr in lines)
                {
                    string line = lineStr.Trim();

                    if (line.StartsWith("====="))
                    {
                        if (line.EndsWith("正则") || line.EndsWith("regex") || line.EndsWith("reg"))
                        {
                            type = BlockType.regex;
                        }
                        else if (line.EndsWith("忽略") || line.EndsWith("ignore"))
                        {
                            type = BlockType.ignore;
                        }
                        else if (line.EndsWith("忽略正则") || line.EndsWith("ignoreRegex") || line.EndsWith("ignoreReg"))
                        {
                            type = BlockType.ignoreRegex;
                        }
                        else if (line.EndsWith("忽略检测") || line.EndsWith("ignoreCheck"))
                        {
                            type = BlockType.ignoreCheck;
                        }
                        else if (line.EndsWith("文本") || line.EndsWith("word") || line.EndsWith("words"))
                        {
                            type = BlockType.word;
                        }
                        continue;
                    }

                    if (line.Equals(""))
                    {
                        continue;
                    }

                    if (type == BlockType.ignore)
                    {
                        ignores.Add(line.Trim());
                    }
                    else if (type == BlockType.ignoreCheck)
                    {
                        ignoreChecks.Add(line.Trim());
                    }
                    else if (type == BlockType.ignoreRegex)
                    {
                        ignoreRegs.Add(new Regex(line.Trim()));
                    }
                    else
                    {
                        string[] result = TextHelper.ReadTranslationLineAndDecode(line);
                        if (result != null)
                        {
                            string key = result[0];
                            string val = result[1];
                            if (!key.Equals("") && !val.Equals(""))
                            {
                                if (type == BlockType.regex)
                                {
                                    regMap.Add(new Regex(key), val);
                                }
                                else
                                {
                                    wordMap.Add(key, val);
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                File.Create(filePath);
            }

            if (File.Exists(outfilePath))
            {
                string[] failLines = File.ReadAllLines(outfilePath);

                foreach (string lineStr in failLines)
                {
                    string line = lineStr.Trim();

                    if (line.Equals(""))
                    {
                        continue;
                    }

                    string[] result = TextHelper.ReadTranslationLineAndDecode(line);
                    if (result != null)
                    {
                        string key = result[0];
                        string val = result[1];
                        if (!key.Equals("") && !val.Equals(""))
                        {
                            notTransMap[key] = val;
                        }
                    }
                }
            }
        }

        private static void rewriteNotTrans() {
            List<string> keys = new List<string>(notTransMap.Keys);

            foreach (string key in keys)
            {
                Result r = _Translate(key, false, false);
                if (r.success)
                {
                    notTransMap.TryRemove(key, out string val);
                }
                else
                {
                    notTransMap[key] = TextHelper.Encode(r.value) + "(" + string.Join(",", r.fails) + ")";
                }
            }

            if (File.Exists(outfilePath))
            {
                File.Delete(outfilePath);
            }

            FileStream fs = File.Open(outfilePath, FileMode.Append);
            StreamWriter sw = new StreamWriter(fs);

            foreach (KeyValuePair<string, string> pair in notTransMap)
            {
                sw.WriteLine(TextHelper.Encode(pair.Key) + '=' + TextHelper.Encode(pair.Value));
            }

            sw.Flush();
            sw.Close();
            fs.Close();
        }

        private static Result _Translate(string oriStr, bool writeNotTrans = true, bool allPass = true)
        {
            bool isFullReplace = false;

            string str = oriStr;

            foreach (Regex reg in ignoreRegs)
            {
                if (reg.Match(str).Success)
                {
                    isFullReplace = true;
                }
            }

            if (!isFullReplace)
            {
                if (wordMap.ContainsKey(str))
                {
                    str = wordMap[str];
                    isFullReplace = true;
                }
            }

            if (!isFullReplace)
            {
                foreach (Regex reg in regMap.Keys)
                {
                    string val = regMap[reg];
                    if (reg.Match(str).Success)
                    {
                        str = reg.Replace(str, val);

                        if (reg.ToString().StartsWith("^") && reg.ToString().EndsWith("$"))
                        {
                            isFullReplace = true;
                        }
                    }
                }
            }

            List<MatchInfo> ignoreMatchs = new List<MatchInfo>();
            if (!isFullReplace)
            {
                foreach (string key in ignores)
                {
                    int last = -1;
                    do
                    {
                        last = str.IndexOf(key, last + 1);
                        if (last != -1)
                        {
                            int before = last - 1;
                            int after = last + key.Length;
                            if (
                                (before < 0 || ((str[before] < 'a' || str[before] > 'z') && (str[before] < 'A' || str[before] > 'Z'))) &&
                                (after >= str.Length || ((str[after] < 'a' || str[after] > 'z') && (str[after] < 'A' || str[after] > 'Z')))
                                )
                            {
                                ignoreMatchs.Add(new MatchInfo(key, "", last, key.Length, false));
                            }
                        }
                    } while (last != -1);
                }
            }

            List<MatchInfo> macths = new List<MatchInfo>();
            if (!isFullReplace)
            {
                foreach (string key in wordMap.Keys)
                {
                    int last = -1;
                    do
                    {
                        last = str.IndexOf(key, last + 1);
                        if (last != -1)
                        {
                            int before = last - 1;
                            int after = last + key.Length;
                            if (
                                (before < 0 || ((str[before] < 'a' || str[before] > 'z') && (str[before] < 'A' || str[before] > 'Z'))) &&
                                (after >= str.Length || ((str[after] < 'a' || str[after] > 'z') && (str[after] < 'A' || str[after] > 'Z')))
                                )
                            {
                                bool notIgnore = true;
                                foreach (MatchInfo ignore in ignoreMatchs)
                                {
                                    if (last >= ignore.index && last < ignore.index + ignore.len)
                                    {
                                        XuaLogger.AutoTranslator.Debug("ignore---");
                                        XuaLogger.AutoTranslator.Debug("last: " + last);
                                        XuaLogger.AutoTranslator.Debug("sss: " + str.Substring(last));
                                        XuaLogger.AutoTranslator.Debug("index: " + ignore.index);
                                        XuaLogger.AutoTranslator.Debug("len: " + ignore.len);
                                        XuaLogger.AutoTranslator.Debug("end: " + (ignore.index + ignore.len));
                                        notIgnore = false;
                                    }
                                }

                                if (notIgnore)
                                {
                                    bool full = (before < 0 && after >= str.Length);
                                    macths.Add(new MatchInfo(key, wordMap[key], last, key.Length, full));
                                }
                                else
                                {
                                    if (oriStr.IndexOf("Auto Save will be overwritten") != -1)
                                    {
                                        XuaLogger.AutoTranslator.Debug("ignore");
                                        XuaLogger.AutoTranslator.Debug(str);
                                        XuaLogger.AutoTranslator.Debug(oriStr);
                                        foreach (var item in ignoreMatchs)
                                        {
                                            XuaLogger.AutoTranslator.Debug(item.key);
                                        }
                                    }
                                }
                            }
                        }
                    } while (last != -1);
                }
            }

            macths.Sort((d1, d2) =>
            {
                if (d1.full)
                {
                    return 1;
                }
                else if (d2.full)
                {
                    return -1;
                }
                else {
                    if (d1.index == d2.index)
                    {
                        return d2.len - d1.len;
                    }
                    else
                    {
                        return d1.index - d2.index;
                    }
                }
            });

            int offset = 0;
            macths.ForEach((m) =>
            {
                if (!isFullReplace)
                {
                    if (m.index + offset >= 0 && m.index + offset + m.len < str.Length && str.Substring(m.index + offset, m.len).Equals(m.key))
                    {
                        str = str.Remove(m.index + offset, m.len).Insert(m.index + offset, m.value);
                        offset += m.value.Length - m.key.Length;
                    }
                }

                if (m.full)
                {
                    isFullReplace = true;
                }
            });

            bool isALLTrans = true;
            List<string> fails = new List<string>();
            if (!isFullReplace)
            {
                MatchCollection notTrans = new Regex("[a-zA-Z]+").Matches(str);
                if (notTrans.Count > 0)
                {
                    foreach (Match m in notTrans)
                    {
                        if (!ignoreChecks.Contains(m.Value))
                        {
                            bool notIgnore = true;
                            foreach (MatchInfo ignore in ignoreMatchs)
                            {
                                if (ignore.key.IndexOf(m.Value) != -1)
                                {
                                    notIgnore = false;
                                }
                            }

                            if (notIgnore)
                            {
                                fails.Add(m.Value);
                                isALLTrans = false;
                            }
                        }
                    }
                }
            }

            if (!isALLTrans)
            {
                if (!notTransMap.ContainsKey(oriStr) && writeNotTrans)
                {
                    FileStream fs = File.Open(outfilePath, FileMode.Append);
                    StreamWriter sw = new StreamWriter(fs);

                    sw.WriteLine(TextHelper.Encode(oriStr) + '=' + TextHelper.Encode(str) + "(" + string.Join(",", fails) + ")");

                    sw.Flush();
                    sw.Close();
                    fs.Close();

                    notTransMap[oriStr] = str;
                }
            }

            //XuaLogger.AutoTranslator.Debug(str);
            //context.Complete("aaa");

            return new Result(allPass ? true : isALLTrans, str, fails);
        }

        public IEnumerator Translate(ITranslationContext context)
        {
            string oriStr = context.UntranslatedText;

            Result r = _Translate(oriStr);

            if (r.success)
            {
                context.Complete(r.value);
            }
            else
            {
                context.Fail("");
            }

            return null;
        }
    }

    struct MatchInfo {
        public string key;
        public string value;
        public int index;
        public int len;
        public bool full;

        public MatchInfo(string key, string value, int index, int len, bool full)
        {
            this.key = key;
            this.value = value;
            this.index = index;
            this.len = len;
            this.full = full;
        }
    }

    struct Result {
        public bool success;
        public string value;
        public List<string> fails;

        public Result(bool success, string value, List<string> fails) {
            this.success = success;
            this.value = value;
            this.fails = fails;
        }
    }

    enum BlockType {
        word,
        regex,
        ignore,
        ignoreRegex,
        ignoreCheck,
    }
}
