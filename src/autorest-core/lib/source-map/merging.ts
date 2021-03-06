/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
import { OutstandingTaskAwaiter } from "../outstanding-task-awaiter";
import { IndexToPosition } from "../parsing/text-utility";
import { ConfigurationView, MessageEmitter } from "../configuration";
import { Message, Channel } from "../message";
import { From } from "../ref/linq";
import { JsonPath, stringify } from "../ref/jsonpath";
import * as yaml from "../ref/yaml";
import { Mapping, Mappings } from "../ref/source-map";
import { DataHandleRead, DataHandleWrite } from "../data-store/data-store";

// // TODO: may want ASTy merge! (supporting circular structure and such?)
function Merge(a: any, b: any, path: JsonPath = []): any {
  if (a === null || b === null) {
    throw new Error(`Argument cannot be null ('${stringify(path)}')`);
  }

  // trivial case
  if (a === b || JSON.stringify(a) === JSON.stringify(b)) {
    return a;
  }

  // mapping nodes
  if (typeof a === "object" && typeof b === "object") {
    if (a instanceof Array && b instanceof Array) {
      // // sequence nodes
      // const result = a.slice();
      // for (const belem of b) {
      //     if (a.indexOf(belem) === -1) {
      //         result.push(belem);
      //     }
      // }
      // return result;
    } else {
      // object nodes - iterate all members
      const result: any = {};
      let keys = Object.getOwnPropertyNames(a).concat(Object.getOwnPropertyNames(b));
      keys = keys.filter((v, i) => { const idx = keys.indexOf(v); return idx === -1 || idx >= i; }); // distinct

      for (const key of keys) {
        const subpath = path.concat(key);

        // forward if only present in one of the nodes
        if (a[key] === undefined) {
          result[key] = b[key];
          continue;
        }
        if (b[key] === undefined) {
          result[key] = a[key];
          continue;
        }

        // try merge objects otherwise
        const aMember = a[key];
        const bMember = b[key];
        result[key] = Merge(aMember, bMember, subpath);
      }
      return result;
    }
  }

  throw new Error(`'${stringify(path)}' has incomaptible values (${yaml.Stringify(a)}, ${yaml.Stringify(b)}).`);
}

export function ShallowCopy(input: any, ...filter: Array<string>): any {
  if (!input) {
    return input;
  }
  const keys = input.Keys ? input.Keys : Object.getOwnPropertyNames(input);

  const result: any = {};
  for (const key of keys) {
    if (filter.indexOf(key) == -1) {
      const value = input[key];
      if (value !== undefined) {
        result[key] = value;
      }
    }
  }
  return result;
}



// Note: I am not convinced this works precisely as it should
// but it works well enough for my needs right now
// I will revisit it later.
const macroRegEx = /\$\(([a-zA-Z0-9_-]*)\)/ig
export function resolveRValue(value: any, propertyName: string, higherPriority: any, lowerPriority: any, jsAware: number = 0): any {
  if (value) {
    // resolves the actual macro value.
    const resolve = (macroExpression: string, macroKey: string) => {
      // if the original set has it, use that.
      if (higherPriority && higherPriority[macroKey]) {
        return resolveRValue(higherPriority[macroKey], macroKey, lowerPriority, null, jsAware - 1);
      }

      if (lowerPriority) {
        // check to see if the value is in the overrides set before the key itself.
        const keys = Object.getOwnPropertyNames(lowerPriority);
        const macroKeyLocation = keys.indexOf(macroKey);
        if (macroKeyLocation > -1) {
          if (macroKeyLocation < keys.indexOf(propertyName)) {
            // the macroKey is in the overrides, and it precedes the propertyName itself
            return resolveRValue(lowerPriority[macroKey], macroKey, higherPriority, lowerPriority, jsAware - 1);
          }
        }
      }

      // can't find the macro. maybe later.
      return macroExpression;
    };

    // resolve the macro value for strings
    if (typeof value === "string") {
      const match = macroRegEx.exec(value.trim());
      if (match) {
        if (match[0] === match.input) {
          // the target value should be the result without string twiddling
          if (jsAware > 0) {
            return `'${resolve(match[0], match[1])}'`;
          }
          return resolve(match[0], match[1]);
        }
        // it looks like we should do a string replace.
        return value.replace(macroRegEx, resolve)
      }
    }

    // resolve macro values for array values
    if (value instanceof Array) {
      const result = [];
      for (const each of value) {
        // since we're not naming the parameter,
        // if there isn't a higher priority,
        // we can fall back to a wide-lookup in lowerPriority.
        result.push(resolveRValue(each, "", higherPriority || lowerPriority, null));
      }
      return result;
    }
  }
  if (jsAware > 0) {
    return `'${value}'`;
  }
  return value;
}

export function MergeOverwriteOrAppend(a: any, b: any, concatListPathFilter: (path: JsonPath) => boolean = _ => false, path: JsonPath = []): any {
  if (a === null || b === null) {
    return null; // TODO: overthink, we could use this to force mute something even if it's "concat" mode...
  }

  // scalars/arrays involved
  if (typeof a !== "object" || a instanceof Array ||
    typeof b !== "object" || b instanceof Array) {
    if (!(a instanceof Array) && !(b instanceof Array) && !concatListPathFilter(path)) {
      return a;
    }
    return a instanceof Array
      ? a.concat(b)
      : [a].concat(b);
  }

  // object nodes - iterate all members
  const result: any = {};
  let keys = Object.getOwnPropertyNames(a).concat(Object.getOwnPropertyNames(b));
  keys = keys.filter((v, i) => { const idx = keys.indexOf(v); return idx === -1 || idx >= i; }); // distinct

  for (const key of keys) {
    const subpath = path.concat(key);

    // forward if only present in one of the nodes
    if (a[key] === undefined) {
      result[key] = resolveRValue(b[key], key, a, b);
      continue;
    }
    if (b[key] === undefined) {
      result[key] = resolveRValue(a[key], key, null, a);
      continue;
    }

    // try merge objects otherwise
    const aMember = resolveRValue(a[key], key, b, a);
    const bMember = resolveRValue(b[key], key, a, b);
    result[key] = MergeOverwriteOrAppend(aMember, bMember, concatListPathFilter, subpath);
  }
  return result;
}

export function IdentitySourceMapping(sourceYamlFileName: string, sourceYamlAst: yaml.YAMLNode): Mappings {
  const result: Mappings = [];
  const descendantsWithPath = yaml.Descendants(sourceYamlAst);
  for (const descendantWithPath of descendantsWithPath) {
    const descendantPath = descendantWithPath.path;
    result.push({
      generated: { path: descendantPath },
      original: { path: descendantPath },
      name: JSON.stringify(descendantPath),
      source: sourceYamlFileName
    });
  }
  return result;
}

export function MergeYamls(config: ConfigurationView, yamlInputHandles: DataHandleRead[], yamlOutputHandle: DataHandleWrite): Promise<DataHandleRead> {
  let resultObject: any = {};
  const mappings: Mappings = [];
  for (const yamlInputHandle of yamlInputHandles) {
    const rawYaml = yamlInputHandle.ReadData();
    resultObject = Merge(resultObject, yaml.Parse(rawYaml, (message, index) => {
      if (config) {
        config.Message({
          Channel: Channel.Error,
          Text: message,
          Source: [{ document: yamlInputHandle.key, Position: IndexToPosition(yamlInputHandle, index) }]
        });
      }
    }) || {});
    mappings.push(...IdentitySourceMapping(yamlInputHandle.key, yamlInputHandle.ReadYamlAst()));
  }

  return yamlOutputHandle.WriteObject(resultObject, mappings, yamlInputHandles);
}