﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using Newtonsoft.Json.Serialization;

namespace ReactiveUI.Serialization
{
    public class JsonNetObjectSerializationProvider : IObjectSerializationProvider
    {
        ThreadLocal<SerializedItemsToGuidResolver> _guidResolver;
        ThreadLocal<JsonSerializer> _serializer;

        /// <summary>
        /// Constructs a new JsonNetObjectSerializationProvider.
        /// </summary>
        /// <param name="engine">The engine to de/serialize dependent sub-objects.</param>
        public JsonNetObjectSerializationProvider(IStorageEngine engine = null)
        {
            _guidResolver = new ThreadLocal<SerializedItemsToGuidResolver>(() =>
                new SerializedItemsToGuidResolver(engine));

            _serializer = new ThreadLocal<JsonSerializer>(() =>
                JsonSerializer.Create(new JsonSerializerSettings() {ContractResolver = _guidResolver.Value }));
        }

        /// <summary>
        /// Write an object to memory, including serializing all of the child
        /// objects in the object graph. In Debug mode, this writes out JSON
        /// using Unicode encoding, and in Release mode this writes out BSON.
        /// </summary>
        /// <param name="obj">The root object to serialize to disk.</param>
        /// <returns>A byte representation of the object.</returns>
        public byte[] Serialize(object obj)
        {
            var ms = new MemoryStream();
            using (var writer = createWriterFromMemoryStream(ms)) {
                _guidResolver.Value.InitializeWithRootObject(obj);
                JsonSerializer
                    .Create(new JsonSerializerSettings() {ContractResolver = _guidResolver.Value})
                    .Serialize(writer, obj);
            }
            var ret = ms.ToArray();
            return ret;
        }

        /// <summary>
        /// Reads an object from the data returned by Serialize.
        /// </summary>
        /// <param name="data">The byte representation of the object.</param>
        /// <param name="type">The type of the object to reconstruct.</param>
        /// <returns>The deserialized object.</returns>
        public object Deserialize(byte[] data, Type type)
        {
            using (var reader = createReaderFromBytes(data)) {
                var dummy = Activator.CreateInstance(type);
                _guidResolver.Value.InitializeWithRootObject(dummy);
                return JsonSerializer
                    .Create(new JsonSerializerSettings() {ContractResolver = _guidResolver.Value})
                    .Deserialize(reader, type);
            }
        }

        /// <summary>
        /// SerializedDataToString is a method used for debugging purposes to
        /// dump a serialized object out as a string. Production
        /// implementations are free to return an empty string.
        /// </summary>
        /// <param name="data">A serialized object to examine.</param>
        /// <returns>The string representation of the byte data (i.e. the JSON
        /// string).</returns>
        public string SerializedDataToString(byte[] data)
        {
#if DEBUG
            return (new StreamReader(new MemoryStream(data))).ReadToEnd();
#else
            return "Run me in debug mode!";
#endif
        }

        static JsonReader createReaderFromBytes(byte[] data)
        {
#if DEBUG
            return new JsonTextReader(new StreamReader(new MemoryStream(data)));
#else
            return new BsonReader(new MemoryStream(data));
#endif
        }

        static JsonWriter createWriterFromMemoryStream(MemoryStream ms)
        {
#if DEBUG
            return new JsonTextWriter(new StreamWriter(ms));
#else
            return new BsonWriter(ms);
#endif
        }
    }

    /// <summary>
    /// This class decides what properties to serialize, and for each property,
    /// whether we should hijack the serializer/deserializer.    
    /// </summary>
    class SerializedItemsToGuidResolver : DefaultContractResolver, IEnableLogger
    {
        SerializableItemConverter _itemConverter;
        SerializableListConverter _listConverter;
        object _rootObject;

        public SerializedItemsToGuidResolver(IStorageEngine engine = null)
        {
            _itemConverter = new SerializableItemConverter(engine);
            _listConverter = new SerializableListConverter(engine);
        }

        protected override JsonContract CreateContract(Type objectType) 
        {
            var ret = base.CreateContract(objectType);
            if (typeof(ISerializableList).IsAssignableFrom(objectType)) {
                return ret;
            }

            if ((typeof(ISerializableItem).IsAssignableFrom(objectType)) &&
                (_rootObject == null || _rootObject.GetType() != objectType)) {
                _rootObject = null;
                ret.Converter = _itemConverter;
            }

            return ret;
        }

        protected override JsonArrayContract CreateArrayContract(Type objectType) 
        {
            var ret = base.CreateArrayContract(objectType);
            if (typeof(ISerializableList).IsAssignableFrom(objectType)) {
                if (_rootObject.GetType() == objectType) {
                    _rootObject = null;
                    ret.Converter = _listConverter;
                } else {
                    ret.Converter = _itemConverter;
                }
            }

            return ret;
        }

        protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization serialization)
        {
            return base.CreateProperties(type, serialization).Where(x => {
                // XXX: This is massively slow and dumb
                var prop = type.GetProperty(x.PropertyName);
                object[] attrs;
                if (prop != null) {
                    attrs = prop.GetCustomAttributes(typeof (IgnoreDataMemberAttribute), true);
                } else {
                    var field = type.GetField(x.PropertyName);
                    attrs = field.GetCustomAttributes(typeof(IgnoreDataMemberAttribute), true);
                }
                return attrs.Length == 0;
            }).ToList();
        }

        public void InitializeWithRootObject(object rootObject)
        {
            _rootObject = rootObject;
        }
    }

    /// <summary>
    /// This class hijacks ISerializableItems and replaces them with their
    /// content hash written out as bytes.
    /// </summary>
    class SerializableItemConverter : JsonConverter
    {
        IStorageEngine _engine;
        public SerializableItemConverter(IStorageEngine engine = null)
        {
            _engine = engine ?? RxStorage.Engine;
        }

        public override bool CanConvert(Type objectType)
        {
            return (typeof (ISerializableItem).IsAssignableFrom(objectType));
        }

        public override object ReadJson(
            JsonReader reader, 
            Type objectType, 
            object existingValue, 
            JsonSerializer serializer)
        {
            // XXX: This is totally borked in Debug mode, fix it!
            if (reader.TokenType != JsonToken.Bytes) {
                throw new Exception(String.Format("Expected bytes, got {0}", reader.Value));
            }

            var contentHash = new Guid((byte[]) reader.Value);
            return _engine.Load(contentHash);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var si = (ISerializableItem)value;
            _engine.Save(si);
            writer.WriteValue(si.ContentHash.ToByteArray());
        }
    }

    class RawSerializedListData {
        public string ItemsRootTypeFullName { get; set; }
        public Dictionary<Guid, DateTimeOffset> CreatedOn { get; set; }
        public Dictionary<Guid, DateTimeOffset> UpdatedOn { get; set; }
        public Guid[] Items { get; set; }
    }

    /// <summary>
    /// This class hijacks ISerializableLists and replaces them with a custom
    /// class representing the actual data.
    /// </summary>
    class SerializableListConverter : JsonConverter
    {
        IStorageEngine _engine;
        public SerializableListConverter(IStorageEngine engine = null)
        {
            _engine = engine ?? RxStorage.Engine;
        }

        public override bool CanConvert(Type objectType) 
        {
            return (typeof (ISerializableList).IsAssignableFrom(objectType));
        }

        public override object ReadJson(
            JsonReader reader, 
            Type objectType, 
            object existingValue, 
            JsonSerializer serializer)
        {
            var toRead = serializer.Deserialize<RawSerializedListData>(reader);
            return null;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) 
        {
            var slist = (ISerializableList)value;
            var toWrite = new RawSerializedListData() {
                CreatedOn = slist.CreatedOn.ToConcreteDictionary(),
                UpdatedOn = slist.UpdatedOn.ToConcreteDictionary(),
                ItemsRootTypeFullName = slist.GetBaseListType().FullName,
                Items = slist.OfType<ISerializableItem>().Select(x => x.ContentHash).ToArray(),
            };

            serializer.Serialize(writer, toWrite);
        }
    }
}

// vim: tw=120 ts=4 sw=4 et :
