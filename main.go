package main

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io/ioutil"
	"net/http"
	"net/url"
	"os"
	"unicode/utf8"

	"github.com/ChimeraCoder/anaconda"
)

var setting struct {
	TwitterConsumerKey, TwitterConsumerSecret,
	TwitterAccessToken, TwitterAccessTokenSecret,
	PushbulletAccessToken string
}

func loadSetting() {
	file, err := os.Open("config.json")
	if err != nil {
		panic(err)
	}
	defer file.Close()
	err = json.NewDecoder(file).Decode(&setting)
	if err != nil {
		panic(err)
	}
}

func onGotTweet(tweet anaconda.Tweet) {
	decoded, err := base64.StdEncoding.DecodeString(tweet.Text)
	if err != nil || !utf8.Valid(decoded) {
		return
	}
	decodedStr := string(decoded)

	body, _ := json.Marshal(
		struct {
			Type  string `json:"type"`
			Title string `json:"title"`
			Body  string `json:"body"`
			URL   string `json:"url"`
		}{
			"link", tweet.User.ScreenName, decodedStr,
			fmt.Sprintf("https://twitter.com/%s/status/%s", tweet.User.ScreenName, tweet.IdStr),
		})
	req, _ := http.NewRequest("POST", "https://api.pushbullet.com/v2/pushes", bytes.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", "Bearer "+setting.PushbulletAccessToken)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		fmt.Println("Push Failed: " + err.Error())
		return
	}
	defer resp.Body.Close()
	respBody, err := ioutil.ReadAll(resp.Body)
	if err != nil {
		fmt.Println("Push Failed: " + err.Error())
		return
	}
	respBodyStr := string(respBody)
	if resp.StatusCode >= 200 && resp.StatusCode <= 299 {
		fmt.Println("Pushed: " + respBodyStr)
	} else {
		fmt.Printf("Push Failed %s: %s\n", resp.Status, respBodyStr)
	}
}

func main() {
	loadSetting()
	anaconda.SetConsumerKey(setting.TwitterConsumerKey)
	anaconda.SetConsumerSecret(setting.TwitterConsumerSecret)
	api := anaconda.NewTwitterApi(setting.TwitterAccessToken, setting.TwitterAccessTokenSecret)

	userStream := api.UserStream(url.Values{})
	fmt.Println("Connected User Stream")
	for {
		event, b := <-userStream.C
		if !b {
			break
		}
		switch t := event.(type) {
		case anaconda.Tweet:
			go onGotTweet(t)
		}
	}
}
