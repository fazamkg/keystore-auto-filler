# Keystore Auto Filler

## How to use

1. Create UnityKeystores folder under Users/you/
2. Places keystores files inside
3. Create keystores.json file
4. Fill it out in this format

```
{
  "entries": [
    {
      "name": "MyGame",
      "keystore": "mygame.keystore",
      "storePass": "secret1",
      "alias": "mygame",
      "aliasPass": "secret1"
    },
    {
      "name": "OtherApp",
      "keystore": "otherapp.keystore",
      "storePass": "secret2",
      "alias": "upload",
      "aliasPass": "secret3"
    }
  ]
}
```

5. Install the package via Package Manager using this repo url
6. Enjoy

Notes:
1. "name" should be exactly the same as unity project name
2. if "keystore" field is empty, then it will use whatever already set in unity project
3. if "alias" field is empty, then it will select first available alias listed from keystore
