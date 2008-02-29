/* 
 *	Copyright (C) 2005-2008 Team MediaPortal
 *	http://www.team-mediaportal.com
 *
 *  This Program is free software; you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation; either version 2, or (at your option)
 *  any later version.
 *   
 *  This Program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 *  GNU General Public License for more details.
 *   
 *  You should have received a copy of the GNU General Public License
 *  along with GNU Make; see the file COPYING.  If not, write to
 *  the Free Software Foundation, 675 Mass Ave, Cambridge, MA 02139, USA. 
 *  http://www.gnu.org/copyleft/gpl.html
 *
 */
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;

using TvDatabase;
using TvLibrary.Log;
using TvLibrary.Implementations;

using Gentle.Common;
using Gentle.Framework;

namespace TvEngine
{
  class XMLTVImport : IComparer
  {
    public delegate void ShowProgressHandler(Stats stats);
    public event ShowProgressHandler ShowProgress;

    class ChannelPrograms
    {
      public string Name;
      public string ExternalId;
      public ArrayList programs = new ArrayList();
    };

    public class Stats
    {
      string _status = "";
      int _programs = 0;
      int _channels = 0;
      DateTime _startTime = DateTime.Now;
      DateTime _endTime = DateTime.Now;
      public string Status
      {
        get { return _status; }
        set { _status = value; }
      }
      public int Programs
      {
        get { return _programs; }
        set { _programs = value; }
      }
      public int Channels
      {
        get { return _channels; }
        set { _channels = value; }
      }
      public DateTime StartTime
      {
        get { return _startTime; }
        set { _startTime = value; }
      }
      public DateTime EndTime
      {
        get { return _endTime; }
        set { _endTime = value; }
      }
    };

    string _errorMessage = "";
    Stats _status = new Stats();
    int _backgroundDelay = 0;

    static bool _isImporting = false;
    public XMLTVImport()
      : this(0)
    {
    }

    public XMLTVImport(int backgroundDelay)
    {
      _backgroundDelay = backgroundDelay;
    }

    public string ErrorMessage
    {
      get { return _errorMessage; }
    }

    public Stats ImportStats
    {
      get { return _status; }
    }

    private int ParseStarRating(string epgRating)
    {
      // format = 5.2/10
      // check if the epgRating is within a xml tag			
      epgRating = epgRating.Trim();
			if (epgRating.Length == 0)
			{
				return -1;
			}

      if (epgRating.StartsWith("<"))
      {
        int endStartTagIdx = epgRating.IndexOf(">") + 1;
        int length = epgRating.IndexOf("</", endStartTagIdx) - endStartTagIdx;
        epgRating = epgRating.Substring(endStartTagIdx,length);
      }
      string strRating = epgRating.Remove(epgRating.IndexOf(@"/"));
      int Rating = -1;
      decimal tmpRating = -1;
      NumberFormatInfo NFO = NumberFormatInfo.InvariantInfo;
      NumberStyles NStyle = NumberStyles.Float;

      if (Decimal.TryParse(strRating, NStyle, NFO, out tmpRating))
        Rating = Convert.ToInt16(tmpRating);
      else
        Log.Info("XMLTVImport: star-rating could not be used - {0},({1})", epgRating, strRating);

      return Rating;
    }

    public bool Import(string fileName, bool showProgress)
    {
			//System.Diagnostics.Debugger.Launch();
      _errorMessage = "";
      if (_isImporting == true)
      {
        _errorMessage = "already importing...";
        return false;
      }
      _isImporting = true;

      TvBusinessLayer layer = new TvBusinessLayer();

      bool result = false;
			XmlTextReader xmlReader = null;


      // remove old programs
      _status.Status = "Removing old programs";
      _status.Channels = 0;
      _status.Programs = 0;
      _status.StartTime = DateTime.Now;
      _status.EndTime = new DateTime(1971, 11, 6);
      if (showProgress && ShowProgress != null) ShowProgress(_status);

      layer.RemoveOldPrograms();

      /*
      // for each channel, get the last program's time
      Dictionary<int, DateTime> lastProgramForChannel = new Dictionary<int, DateTime>();
      IList channels = Channel.ListAll();
      foreach (Channel ch in channels)
      {
        SqlBuilder sb = new SqlBuilder(StatementType.Select, typeof(TvDatabase.Program));
        sb.AddConstraint(Operator.Equals, "idChannel", ch.IdChannel);
        sb.AddOrderByField(false, "starttime");
        sb.SetRowLimit(1);
        SqlStatement stmt = sb.GetStatement(true);
        IList programsInDbs = ObjectFactory.GetCollection(typeof(TvDatabase.Program), stmt.Execute());

        DateTime lastProgram = DateTime.MinValue;
        if (programsInDbs.Count > 0)
        {
          TvDatabase.Program p = (TvDatabase.Program)programsInDbs[0];
          lastProgram = p.EndTime;
        }
        lastProgramForChannel[ch.IdChannel] = lastProgram;
      }*/

      //TVDatabase.SupressEvents = true;
      bool useTimeZone = false;
      int timeZoneCorrection = 0;
      useTimeZone = layer.GetSetting("xmlTvUseTimeZone", "true").Value == "true";
      int hours = Int32.Parse(layer.GetSetting("xmlTvTimeZoneHours", "0").Value);
      int mins = Int32.Parse(layer.GetSetting("xmlTvTimeZoneMins", "0").Value);
      timeZoneCorrection = hours * 60 + mins;

      ArrayList Programs = new ArrayList();
      Dictionary<int,ChannelPrograms> dChannelPrograms = new Dictionary<int,ChannelPrograms>();

      try
      {
        Log.WriteFile("xmltv import {0}", fileName);

        //
        // Make sure the file exists before we try to do any processing
        //
        if (File.Exists(fileName))
        {
          _status.Status = "Loading channel list";
          _status.Channels = 0;
          _status.Programs = 0;
          _status.StartTime = DateTime.Now;
          _status.EndTime = new DateTime(1971, 11, 6);
          if (showProgress && ShowProgress != null) ShowProgress(_status);

          Dictionary<int, Channel> guideChannels = new Dictionary<int, Channel>();

          IList allChannels = Channel.ListAll();

          int iChannel = 0;

          xmlReader = new XmlTextReader(fileName);					

          #region import non-mapped channels by their display-name
          if (xmlReader.ReadToDescendant("tv"))
          {
            // get the first channel
            if (xmlReader.ReadToDescendant("channel"))
            {
              do
              {
                String id = xmlReader.GetAttribute("id");
                if (id == null || id.Length == 0)
                {
                  Log.Error("  channel#{0} doesnt contain an id", iChannel);
                }
                else
                {
                  String displayName = null;

                  XmlReader xmlChannel = xmlReader.ReadSubtree();
                  xmlChannel.ReadStartElement();  // read channel
                  // now, xmlChannel is positioned on the first sub-element of <channel>
                  while (!xmlChannel.EOF)
                  {
                    if (xmlChannel.NodeType == XmlNodeType.Element)
                    {
                      switch (xmlChannel.Name)
                      {
                        case "display-name":
                        case "Display-Name":
                          if (displayName == null) displayName = xmlChannel.ReadString(); else xmlChannel.Skip();
                          break;
                        // could read more stuff here, like icon...
                        default:
                          // unknown, skip entire node
                          xmlChannel.Skip();
                          break;
                      }
                    }
                    else
                      xmlChannel.Read();
                  }
									if (xmlChannel != null)
									{
										xmlChannel.Close();
										xmlChannel = null;

									}									

                  if (displayName == null || displayName.Length == 0)
                  {
                    Log.Error("  channel#{0} xmlid:{1} doesnt contain an displayname", iChannel, id);
                  }
                  else
                  {
                   Channel chan = null;

                    // a guide channel can be mapped to multiple tvchannels
                    foreach (Channel ch in allChannels)
                    {
                      if (ch.ExternalId == id)
                      {
                        chan = ch;
                        chan.ExternalId = id;
                      }

                      if (chan == null)
                      {
                        // no mapping found, ignore channel
                        continue;
                      }

                      ChannelPrograms newProgChan = new ChannelPrograms();
                      newProgChan.Name = chan.Name;
                      newProgChan.ExternalId = chan.ExternalId;
                      Programs.Add(newProgChan);

                      Log.WriteFile("  channel#{0} xmlid:{1} name:{2} dbsid:{3}", iChannel, chan.ExternalId, chan.Name, chan.IdChannel);
                      if (!guideChannels.ContainsKey(chan.IdChannel))
                      {
                        guideChannels.Add(chan.IdChannel, chan);
                        dChannelPrograms.Add(chan.IdChannel, newProgChan);

                      }
                    }

                    _status.Channels++;
                    if (showProgress && ShowProgress != null) ShowProgress(_status);
                  }
                }
                iChannel++;
                // get the next channel
              } while (xmlReader.ReadToNextSibling("channel"));
            }
          }

          //xmlReader.Close();
          #endregion

          SqlBuilder sb = new SqlBuilder(StatementType.Select, typeof(Channel));
          sb.AddOrderByField(true, "externalId");
          sb.AddConstraint("externalId IS NOT null");
          sb.AddConstraint(Operator.NotEquals, "externalId", "");
          
          SqlStatement stmt = sb.GetStatement(true);
          allChannels = ObjectFactory.GetCollection(typeof(Channel), stmt.Execute());
          if (allChannels.Count == 0)
          {
            _isImporting = false;
						if (xmlReader != null)
						{
							xmlReader.Close();
							xmlReader = null;
						}

            return true;
          }
          
          ///////////////////////////////////////////////////////////////////////////
          /*  design:
           * 1. create a Dictionary<string,Channel> using the externalid as the key,
           *    add all channels to this Dictionary 
           *    Note: channel -> guidechannel is a one-to-many relationship. 
           * 2. Read all programs from the xml file
           * 3. Create a program for each mapped channel
           */
          ///////////////////////////////////////////////////////////////////////////
         Dictionary<string, List<Channel>> allChannelMappingsByExternalId = new Dictionary<string, List<Channel>>();

         string previousExternalId = null;
          // one-to-many so we need a collection of channels for each externalId
         List<Channel> eidMappedChannels = new List<Channel>();

          for(int i = 0; i < allChannels.Count; i++)
          {
            Channel ch = (Channel)allChannels[i];

            if (previousExternalId == null)
            {
              eidMappedChannels.Add(ch);
              previousExternalId = ch.ExternalId;
            }
            else if (ch.ExternalId == previousExternalId)
            {
              eidMappedChannels.Add(ch);
            }
            else
            {
              // got all channels for this externalId. Add the mappings
              allChannelMappingsByExternalId.Add(previousExternalId, eidMappedChannels);
              // new externalid, create a new List & add the channel to the new List
              eidMappedChannels = new List<Channel>();
              eidMappedChannels.Add(ch);
              previousExternalId = ch.ExternalId;
            }
            
            if (i == allChannels.Count - 1)
            {
              allChannelMappingsByExternalId.Add(previousExternalId, eidMappedChannels);
            }
          }
          
          int programIndex = 0;
          _status.Status = "Loading TV programs";
          if (showProgress && ShowProgress != null) ShowProgress(_status);

          Log.Debug("xmltvimport: Reading TV programs");
					if (xmlReader != null)
					{
						xmlReader.Close();
						xmlReader = null;
					}
          xmlReader = new XmlTextReader(fileName);
          if (xmlReader.ReadToDescendant("tv"))
          {
            // get the first programme
            if (xmlReader.ReadToDescendant("programme"))
            {
           
              #region read programme node
              do
              {
                ChannelPrograms channelPrograms = new ChannelPrograms();

                String nodeStart = xmlReader.GetAttribute("start");
                String nodeStop = xmlReader.GetAttribute("stop");
                String nodeChannel = xmlReader.GetAttribute("channel");

                String nodeTitle = null;
                String nodeCategory = null;
                String nodeDescription = null;
                String nodeEpisode = null;
                String nodeRepeat = null;
                String nodeEpisodeNum = null;
                String nodeEpisodeNumSystem = null;
                String nodeDate = null;
                String nodeStarRating = null;
                String nodeClassification = null;

                XmlReader xmlProg = xmlReader.ReadSubtree();
                xmlProg.ReadStartElement();  // read programme
                // now, xmlProg is positioned on the first sub-element of <programme>
                while (!xmlProg.EOF)
                {
                  if (xmlProg.NodeType == XmlNodeType.Element)
                  {
                    switch (xmlProg.Name)
                    {
                      case "title": if (nodeTitle == null) nodeTitle = xmlProg.ReadString(); else xmlProg.Skip(); break;
											case "category": if (nodeCategory == null) nodeCategory = xmlProg.ReadString(); else xmlProg.Skip(); break;
											case "desc": if (nodeDescription == null) nodeDescription = xmlProg.ReadString(); else xmlProg.Skip(); break;
											case "sub-title": if (nodeEpisode == null) nodeEpisode = xmlProg.ReadString(); else xmlProg.Skip(); break;
											case "previously-shown": if (nodeRepeat == null) nodeRepeat = xmlProg.ReadString(); else xmlProg.Skip(); break;
                      case "episode-num":
                        if (nodeEpisodeNum == null)
                        {
                          nodeEpisodeNumSystem = xmlProg.GetAttribute("system");
													nodeEpisodeNum = xmlProg.ReadString();
                        }
                        else xmlProg.Skip();
                        break;
											case "date": if (nodeDate == null) nodeDate = xmlProg.ReadString(); else xmlProg.Skip(); break;
											case "star-rating": if (nodeStarRating == null) nodeStarRating = xmlProg.ReadInnerXml(); else xmlProg.Skip(); break;
											case "rating": if (nodeClassification == null) nodeClassification = xmlProg.ReadInnerXml(); else xmlProg.Skip(); break;
                      default:
                        // unknown, skip entire node
                        xmlProg.Skip();
                        break;
                    }
                  }
                  else
                    xmlProg.Read();
                }
								if (xmlProg != null)
								{
									xmlProg.Close();
									xmlProg = null;
								}
              #endregion

                #region verify/convert values (programme)

                if (nodeStart != null && nodeChannel != null && nodeTitle != null &&
                  nodeStart.Length > 0 && nodeChannel.Length > 0 && nodeTitle.Length > 0)
                {
                  string description = "";
                  string category = "-";
                  string episode = "";
                  string serEpNum = "";
                  string date = "";
                  string seriesNum = "";
                  string episodeNum = "";
                  string episodePart = "";
                  int starRating = -1;
                  string classification = "";

                  string title = ConvertHTMLToAnsi(nodeTitle);

                  long startDate = 0;
                  if (nodeStart.Length >= 14)
                  {
                    if (Char.IsDigit(nodeStart[12]) && Char.IsDigit(nodeStart[13]))
                      startDate = Int64.Parse(nodeStart.Substring(0, 14));//20040331222000
                    else
                      startDate = 100 * Int64.Parse(nodeStart.Substring(0, 12));//200403312220
                  }
                  else if (nodeStart.Length >= 12)
                  {
                    startDate = 100 * Int64.Parse(nodeStart.Substring(0, 12));//200403312220
                  }

                  long stopDate = startDate;
                  if (nodeStop != null)
                  {
                    if (nodeStop.Length >= 14)
                    {
                      if (Char.IsDigit(nodeStop[12]) && Char.IsDigit(nodeStop[13]))
                        stopDate = Int64.Parse(nodeStop.Substring(0, 14));//20040331222000
                      else
                        stopDate = 100 * Int64.Parse(nodeStop.Substring(0, 12));//200403312220
                    }
                    else if (nodeStop.Length >= 12)
                    {
                      stopDate = 100 * Int64.Parse(nodeStop.Substring(0, 12));//200403312220
                    }
                  }

                  startDate = CorrectIllegalDateTime(startDate);
                  stopDate = CorrectIllegalDateTime(stopDate);
                  string timeZoneStart = "";
                  string timeZoneEnd = "";
                  if (nodeStart.Length > 14)
                  {
                    timeZoneStart = nodeStart.Substring(14);
                    timeZoneStart = timeZoneStart.Trim();
                    timeZoneEnd = timeZoneStart;
                  }
                  if (nodeStop != null)
                  {
                    if (nodeStop.Length > 14)
                    {
                      timeZoneEnd = nodeStop.Substring(14);
                      timeZoneEnd = timeZoneEnd.Trim();
                    }
                  }

                  // add timezone correction
                  // correct program starttime
                  DateTime dateTimeStart = longtodate(startDate);
                  dateTimeStart = dateTimeStart.AddMinutes(timeZoneCorrection);

                  if (useTimeZone)
                  {
                    int off = GetTimeOffset(timeZoneStart);
                    int h = off / 100;                // 220 -> 2,  -220 -> -2
                    int m = off - (h * 100);     // 220 -> 20, -220 -> -20

                    // convert to UTC
                    dateTimeStart = dateTimeStart.AddHours(-h);
                    dateTimeStart = dateTimeStart.AddMinutes(-m);

                    // and back to local time
                    dateTimeStart = dateTimeStart.ToLocalTime();
                  }

                  startDate = datetolong(dateTimeStart);


                  if (nodeStop != null)
                  {
                    // correct program endtime
                    DateTime dateTimeEnd = longtodate(stopDate);
                    dateTimeEnd = dateTimeEnd.AddMinutes(timeZoneCorrection);

                    if (useTimeZone)
                    {
                      int off = GetTimeOffset(timeZoneEnd);
                      int h = off / 100;                // 220 -> 2,  -220 -> -2
                      int m = off - (h * 100);     // 220 -> 20, -220 -> -20

                      // convert to UTC
                      dateTimeEnd = dateTimeEnd.AddHours(-h);
                      dateTimeEnd = dateTimeEnd.AddMinutes(-m);

                      // and back to local time
                      dateTimeEnd = dateTimeEnd.ToLocalTime();
                    }

                    stopDate = datetolong(dateTimeEnd);
                  }
                  else stopDate = startDate;

                  //int channelId = -1;
                  //string channelName = "";

                  if (nodeCategory != null)
                    category = nodeCategory;

                  if (nodeDescription != null)
                  {
                    description = ConvertHTMLToAnsi(nodeDescription);
                  }
                  if (nodeEpisode != null)
                  {
                    episode = ConvertHTMLToAnsi(nodeEpisode);
                    if (title.Length == 0)
                      title = nodeEpisode;
                  }

                  if (nodeEpisodeNum != null)
                  {
                    if (nodeEpisodeNumSystem != null && nodeEpisodeNumSystem == "xmltv_ns")
                    {
                      serEpNum = ConvertHTMLToAnsi(nodeEpisodeNum.Replace(" ", ""));
                      int pos = 0;
                      int Epos = 0;
                      pos = serEpNum.IndexOf(".", pos);
                      if (pos == 0) //na_dd grabber only gives '..0/2' etc
                      {
                        Epos = pos;
                        pos = serEpNum.IndexOf(".", pos + 1);
                        episodeNum = serEpNum.Substring(Epos + 1, (pos - 1) - Epos);
                        episodePart = serEpNum.Substring(pos + 1, serEpNum.Length - (pos + 1));
                        if (episodePart.IndexOf("/", 0) != -1)// danish guide gives: episode-num system="xmltv_ns"> . 113 . </episode-num>
                        {
                          if (episodePart.Substring(2, 1) == "1") episodePart = "";
                          else
                          {
                            int p = 0;
                            int t = 0;

                            if (Convert.ToInt32(episodePart.Substring(0, 1)) == 0)
                            {
                              p = Convert.ToInt32(episodePart.Substring(0, 1)) + 1;
                              t = Convert.ToInt32(episodePart.Substring(2, 1));
                              episodePart = Convert.ToString(p) + "/" + Convert.ToString(t);
                            }
                          }
                        }
                      }
                      else if (pos > 0)
                      {
                        seriesNum = serEpNum.Substring(0, pos);
                        Epos = pos;
                        pos = serEpNum.IndexOf(".", pos + 1);
                        episodeNum = serEpNum.Substring(Epos + 1, (pos - 1) - Epos);
                        episodePart = serEpNum.Substring(pos + 1, serEpNum.Length - (pos + 1));
                        if (episodePart.IndexOf("/", 0) != -1)
                        {
                          if (episodePart.Substring(2, 1) == "1") episodePart = "";
                          else
                          {
                            int p = 0;
                            int t = 0;
                            if (Convert.ToInt32(episodePart.Substring(0, 1)) == 0)
                            {
                              p = Convert.ToInt32(episodePart.Substring(0, 1)) + 1;
                            }
                            else
                            {
                              p = Convert.ToInt32(episodePart.Substring(0, 1));
                            }
                            t = Convert.ToInt32(episodePart.Substring(2, 1));
                            episodePart = Convert.ToString(p) + "/" + Convert.ToString(t);
                          }
                        }
                      }
                      else
                      {
                        seriesNum = serEpNum;
                        episodeNum = "";
                        episodePart = "";
                      }
                    }
                  }

                  if (nodeDate != null)
                  {
                    date = nodeDate;
                  }

                  if (nodeStarRating != null)
                  {
                    starRating = ParseStarRating(nodeStarRating);
                  }

                  if (nodeClassification != null)
                  {
                    classification = nodeClassification;
                  }

                  if (showProgress && ShowProgress != null && (_status.Programs % 100) == 0) ShowProgress(_status);

                #endregion

                  #region create a program for every mapped channel

                  List<Channel> mappedChannels;

                  if (allChannelMappingsByExternalId.ContainsKey(nodeChannel))
                  {
                    mappedChannels = allChannelMappingsByExternalId[nodeChannel];
                    if (mappedChannels != null && mappedChannels.Count > 0)
                    {
                      foreach (Channel chan in mappedChannels)
                      {
                        // get the channel program
                        channelPrograms = dChannelPrograms[chan.IdChannel];
                      
                        if (chan.IdChannel < 0)
                        {
                          continue;
                        }

                        Program prog = new Program(chan.IdChannel, longtodate(startDate), longtodate(stopDate), title, description, category, false, DateTime.MinValue, seriesNum, episodeNum, starRating, classification,-1);
                        channelPrograms.programs.Add(prog);
                        programIndex++;
                        //prog.Description = ConvertHTMLToAnsi(strDescription);
                        //prog.StartTime = iStart;
                        //prog.EndTime = iStop;
                        //prog.Title = ConvertHTMLToAnsi(strTitle);
                        //prog.Genre = ConvertHTMLToAnsi(strCategory);
                        //prog.Channel = ConvertHTMLToAnsi(strChannelName);
                        //prog.Date = strDate;
                        //prog.Episode = ConvertHTMLToAnsi(strEpisode);
                        //prog.Repeat = ConvertHTMLToAnsi(strRepeat);
                        //prog.SeriesNum = ConvertHTMLToAnsi(strSeriesNum);
                        //prog.EpisodeNum = ConvertHTMLToAnsi(strEpisodeNum);
                        //prog.EpisodePart = ConvertHTMLToAnsi(strEpisodePart);
                        //prog.StarRating = ConvertHTMLToAnsi(strStarRating);
                        //prog.Classification = ConvertHTMLToAnsi(strClasification);
                        _status.Programs++;
                      }
                    }
                  }
                }
                // get the next programme
              } while (xmlReader.ReadToNextSibling("programme"));							
							//if (xmlReader != null) xmlReader.Close();

                  #endregion

              #region sort & remove invalid programs. Save all valid programs
              Log.Debug("xmltvimport: Sorting TV programs");

              _status.Programs = 0;
              _status.Status = "Sorting TV programs";
              if (showProgress && ShowProgress != null) ShowProgress(_status);
              DateTime dtStartDate = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 0, 0, 0, 0);
              //dtStartDate=dtStartDate.AddDays(-4);

             
              foreach (ChannelPrograms progChan in Programs)
              {
                // empty, skip it
                if (progChan.programs.Count == 0) continue;

                progChan.programs.Sort(this);
                for (int i = 0; i < progChan.programs.Count; ++i)
                {
                  Program prog = (Program)progChan.programs[i];
                  if (prog.StartTime == prog.EndTime)
                  {
                    if (i + 1 < progChan.programs.Count)
                    {
                      Program progNext = (Program)progChan.programs[i + 1];
                      prog.EndTime = progNext.StartTime;
                    }
                  }
                }

                RemoveOverlappingPrograms(ref progChan.programs); // be sure that we do not have any overlapping

                // get the id of the channel, just get the IdChannel of the first program
                int idChannel = ((Program)progChan.programs[0]).IdChannel;

                // retrieve all programs for this channel
                ArrayList programs = new ArrayList();
                {
                  SqlBuilder sb2 = new SqlBuilder(StatementType.Select, typeof(TvDatabase.Program));
                  sb2.AddConstraint(Operator.Equals, "idChannel", idChannel);
                  sb2.AddOrderByField(false, "starttime");
                  SqlStatement stmt2 = sb2.GetStatement(true);
                  IList programsInDbs = ObjectFactory.GetCollection(typeof(TvDatabase.Program), stmt2.Execute());

                  int count = programsInDbs.Count;
                  programs.Capacity = count;
                  for (int i = 0; i < count; i++)
                    programs.Add(programsInDbs[i]);
                }

                List<Program> importProgs = new List<Program>(progChan.programs.Count);

                for (int i = 0; i < progChan.programs.Count; ++i)
                {
                  Program prog = (Program)progChan.programs[i];
                  // don't import programs which have already ended...
                  if (prog.EndTime > dtStartDate)
                  {
                    DateTime start = prog.StartTime;
                    DateTime end = prog.EndTime;

                    bool overlaps = false;

                    // check whether there exists a program that overlaps with prog
                    for (int j = 0; j < programs.Count; j++)
                    {
                      Program proggi = (Program)programs[j];
                      if (proggi.RunningAt(start, end))
                      {
                        overlaps = true;
                        break;
                      }
                    }

                    if (!overlaps)
                    {
                      importProgs.Add(prog);

                      //try
                      //{
                      //  prog.Persist();
                      //}
                      //catch (Exception e)
                      //{
                      //  Log.Error("Error while saving {0}", prog.IdChannel + ":" + prog.Title + ":" + prog.Description);
                      //  throw e;
                      //}
                      //Thread.Sleep(_backgroundDelay);
                    }

                    if (prog.StartTime < _status.StartTime)
                      _status.StartTime = prog.StartTime;
                    if (prog.EndTime > _status.EndTime)
                      _status.EndTime = prog.EndTime;
                    _status.Programs++;
                    if (showProgress && ShowProgress != null && (_status.Programs % 100) == 0) ShowProgress(_status);
                  }
                }
                layer.InsertPrograms(importProgs, ThreadPriority.AboveNormal);
              }
            }
              #endregion

            //TVDatabase.RemoveOverlappingPrograms();

            //TVDatabase.SupressEvents = false;
            if (programIndex > 0)
            {
              _errorMessage = "File imported successfully";
              result = true;
            }
            else
              _errorMessage = "No programs found";
          }
        }
        else
        {
          _errorMessage = "No xmltv file found";
          _status.Status = _errorMessage;
          Log.Error("xmltv data file was not found");
        }
      }
      catch (Exception ex)
      {
        _errorMessage = String.Format("Invalid XML file:{0}", ex.Message);
        _status.Status = String.Format("invalid XML file:{0}", ex.Message);
        Log.Error("XML tv import error loading {0} err:{1} \n {2}", fileName, ex.Message, ex.StackTrace);

        //TVDatabase.RollbackTransaction();
      }

      Programs.Clear();
      Programs = null;
      _isImporting = false;
      //      TVDatabase.SupressEvents = false;
			if (xmlReader != null)
			{
				xmlReader.Close();
				xmlReader = null;
			}
      return result;
    }

    int GetTimeOffset(string timeZone)
    {
      // timezone can b in format:
      // GMT +0100 or GMT -0500
      // or just +0300
      if (timeZone.Length == 0) return 0;
      timeZone = timeZone.ToLower();

      // just ignore GMT offsets, since we're calculating everything from GMT anyway
      if (timeZone.IndexOf("gmt") >= 0)
      {
        int ipos = timeZone.IndexOf("gmt");
        timeZone = timeZone.Substring(ipos + "GMT".Length);
      }

      timeZone = timeZone.Trim();
      if (timeZone[0] == '+' || timeZone[0] == '-')
      {
        string strOff = timeZone.Substring(1);
        try
        {
          int iOff = Int32.Parse(strOff);
          if (timeZone[0] == '-') return -iOff;
          else return iOff;
        }
        catch (Exception)
        {
        }
      }
      return 0;
    }

    long CorrectIllegalDateTime(long datetime)
    {
      //format : 20050710245500
      long orgDateTime = datetime;
      long sec = datetime % 100; datetime /= 100;
      long min = datetime % 100; datetime /= 100;
      long hour = datetime % 100; datetime /= 100;
      long day = datetime % 100; datetime /= 100;
      long month = datetime % 100; datetime /= 100;
      long year = datetime;
      DateTime dt = new DateTime((int)year, (int)month, (int)day, 0, 0, 0);
      dt = dt.AddHours(hour);
      dt = dt.AddMinutes(min);
      dt = dt.AddSeconds(sec);


      long newDateTime = datetolong(dt);
      if (sec < 0 || sec > 59 ||
        min < 0 || min > 59 ||
        hour < 0 || hour >= 24 ||
        day < 0 || day > 31 ||
        month < 0 || month > 12)
      {
        //Log.WriteFile(LogType.EPG, true, "epg-import:tvguide.xml contains invalid date/time :{0} converted it to:{1}",
        //              orgDateTime, newDateTime);
      }

      return newDateTime;
    }

    public void RemoveOverlappingPrograms(ref ArrayList Programs)
    {
      try
      {
        if (Programs.Count == 0) return;
        Programs.Sort(this);
        Program prevProg = (Program)Programs[0];
        for (int i = 1; i < Programs.Count; i++)
        {
          Program newProg = (Program)Programs[i];
          if (newProg.StartTime < prevProg.EndTime)   // we have an overlap here
          {
            // let us find out which one is the correct one
            if (newProg.StartTime > prevProg.StartTime)  // newProg will create hole -> delete it
            {
              Programs.Remove(newProg);
              i--;                              // stay at the same position
              continue;
            }

            List<Program> prevList = new List<Program>();
            List<Program> newList = new List<Program>();
            prevList.Add(prevProg);
            newList.Add(newProg);
            Program syncPrev = prevProg;
            Program syncProg = newProg;
            for (int j = i + 1; j < Programs.Count; j++)
            {
              Program syncNew = (Program)Programs[j];
              if (syncPrev.EndTime == syncNew.StartTime)
              {
                prevList.Add(syncNew);
                syncPrev = syncNew;
                if (syncNew.StartTime > syncProg.EndTime)
                {
                  // stop point reached => delete Programs in newList
                  foreach (Program Prog in newList) Programs.Remove(Prog);
                  i = j - 1;
                  prevProg = syncPrev;
                  newList.Clear();
                  prevList.Clear();
                  break;
                }
              }
              else if (syncProg.EndTime == syncNew.StartTime)
              {
                newList.Add(syncNew);
                syncProg = syncNew;
                if (syncNew.StartTime > syncPrev.EndTime)
                {
                  // stop point reached => delete Programs in prevList
                  foreach (Program Prog in prevList) Programs.Remove(Prog);
                  i = j - 1;
                  prevProg = syncProg;
                  newList.Clear();
                  prevList.Clear();
                  break;
                }
              }
            }
            // check if a stop point was reached => if not delete newList
            if (newList.Count > 0)
            {
              foreach (Program Prog in prevList) Programs.Remove(Prog);
              i = Programs.Count;
              break;
            }
          }
          prevProg = newProg;
        }
      }
      catch (Exception ex)
      {
        Log.Error("XML tv import error:{1} \n {2} ", ex.Message, ex.StackTrace);
      }
    }

    public void FillInMissingDataFromDB(ref ArrayList Programs, ArrayList dbEPG)
    {
      Programs.Sort(this);
      dbEPG.Sort(this);
      Program prevProg = (Program)Programs[0];
      for (int i = 1; i < Programs.Count; i++)
      {
        Program newProg = (Program)Programs[i];
        if (newProg.StartTime > prevProg.EndTime)   // we have a gab here
        {
          // try to find data in the database
          foreach (Program dbProg in dbEPG)
          {
            if ((dbProg.StartTime >= prevProg.EndTime) && (dbProg.EndTime <= newProg.StartTime))
            {
              Programs.Insert(i, dbProg.Clone());
              i++;
              prevProg = dbProg;
            }
            if (dbProg.StartTime >= newProg.EndTime) break; // no more data available
          }
        }
        prevProg = newProg;
      }
    }



    public long datetolong(DateTime dt)
    {
      try
      {
        long iSec = 0;//(long)dt.Second;
        long iMin = (long)dt.Minute;
        long iHour = (long)dt.Hour;
        long iDay = (long)dt.Day;
        long iMonth = (long)dt.Month;
        long iYear = (long)dt.Year;

        long lRet = (iYear);
        lRet = lRet * 100L + iMonth;
        lRet = lRet * 100L + iDay;
        lRet = lRet * 100L + iHour;
        lRet = lRet * 100L + iMin;
        lRet = lRet * 100L + iSec;
        return lRet;
      }
      catch (Exception)
      {
      }
      return 0;
    }
    public DateTime longtodate(long ldate)
    {
      try
      {
        if (ldate < 0) return DateTime.MinValue;
        int year, month, day, hour, minute, sec;
        sec = (int)(ldate % 100L); ldate /= 100L;
        minute = (int)(ldate % 100L); ldate /= 100L;
        hour = (int)(ldate % 100L); ldate /= 100L;
        day = (int)(ldate % 100L); ldate /= 100L;
        month = (int)(ldate % 100L); ldate /= 100L;
        year = (int)ldate;
        DateTime dt = new DateTime(year, month, day, hour, minute, 0, 0);
        return dt;
      }
      catch (Exception)
      {
      }
      return DateTime.Now;
    }

    public string ConvertHTMLToAnsi(string html)
    {
      string strippedHtml = String.Empty;
      ConvertHTMLToAnsi(html, out strippedHtml);
      return strippedHtml;
    }
    public void ConvertHTMLToAnsi(string html, out string strippedHtml)
    {
      strippedHtml = "";
      //	    int i=0; 
      if (html.Length == 0)
      {
        strippedHtml = "";
        return;
      }
      //int iAnsiPos=0;
      StringWriter writer = new StringWriter();

      System.Web.HttpUtility.HtmlDecode(html, writer);

      String DecodedString = writer.ToString();
      strippedHtml = DecodedString.Replace("<br>", "\n");
      if (true)
        return;
    }
    #region Sort Members


    public int Compare(object x, object y)
    {
      if (x == y) return 0;
      Program item1 = (Program)x;
      Program item2 = (Program)y;
      if (item1 == null) return -1;
      if (item2 == null) return -1;
      
      if (item1.IdChannel != item2.IdChannel)
      {
        return String.Compare(item1.ReferencedChannel().Name, item2.ReferencedChannel().Name, true);
      }
      if (item1.StartTime > item2.StartTime) return 1;
      if (item1.StartTime < item2.StartTime) return -1;
      return 0;
    }
    #endregion


  }
}
