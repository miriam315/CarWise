import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import ts from 'typescript';
const source=await readFile(new URL('../src/carImageBatch.ts',import.meta.url),'utf8');
const {outputText}=ts.transpileModule(source,{compilerOptions:{target:ts.ScriptTarget.ES2022,module:ts.ModuleKind.ES2022}});
const {matchCarImagePath}=await import('data:text/javascript;base64,'+Buffer.from(outputText).toString('base64'));
test('Bulk image mapping uses exact catalogue IDs, explicit media types and folder or flat paths',()=>{
 const cars=[{id:'CAR-01'},{id:'CAR-02'}];
 for(const [path,expected] of [
  ['CAR-01__exterior__01.jpg',{carId:'CAR-01',view:'exterior'}],
  ['fleet/CAR-02/interior/01.png',{carId:'CAR-02',view:'interior'}],
  ['fleet\\CAR-01\\spin\\01.jpg',{carId:'CAR-01',view:'spin'}],
  ['car-02__360__12.webp',{carId:'CAR-02',view:'spin'}],
  ['CAR-010__exterior__01.jpg',{carId:'',view:'exterior'}],
  ['Toyota Corolla.jpg',{carId:'',view:'exterior'}],
 ])assert.deepEqual(matchCarImagePath(path,cars),expected,path);
});
